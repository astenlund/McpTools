"""Four Google Keep operations on a single disposable authenticated client."""

import hashlib
import json
import re
from typing import Any

import gkeepapi
from gkeepapi.node import List, NodeAudio, NodeDrawing, NodeImage, Note

from mcp_keep.config import read_credentials
from mcp_keep.errors import AUTH_FAILED, NOTE_UNAVAILABLE, UNCERTAIN_WRITE, KeepError
from mcp_keep.sync_boundary import SyncBoundary

ARCHIVE_STATES = ("active", "archived", "all")
OPERATIONS = ("list_notes", "read_note", "archive_note", "unarchive_note")
ATTACHMENT_KINDS = {NodeImage: "images", NodeDrawing: "drawings", NodeAudio: "audio"}


def validate_request(operation: str, arguments: dict[str, Any]) -> None:
    if operation not in OPERATIONS:
        raise KeepError("Unsupported Google Keep operation.")
    if operation == "list_notes":
        if arguments.get("state", "active") not in ARCHIVE_STATES:
            raise KeepError("state must be active, archived, or all.")
        limit = arguments.get("limit", 50)
        offset = arguments.get("offset", 0)
        query = arguments.get("query", "")
        if type(limit) is not int or not 1 <= limit <= 200:
            raise KeepError("limit must be an integer between 1 and 200.")
        if type(offset) is not int or offset < 0:
            raise KeepError("offset must be a nonnegative integer.")
        if not isinstance(query, str) or len(query) > 1000:
            raise KeepError("query must be text of at most 1000 characters.")
    else:
        note_id = arguments.get("note_id")
        if not isinstance(note_id, str) or not note_id.strip() or note_id != note_id.strip() or len(note_id) > 256:
            raise KeepError("Provide a nonempty note_id from list_notes, without surrounding whitespace.")
    if "expected_fingerprint" in arguments and (
        operation != "archive_note"
        or not isinstance(arguments["expected_fingerprint"], str)
        or re.fullmatch(r"[0-9a-f]{64}", arguments["expected_fingerprint"]) is None
    ):
        raise KeepError("expected_fingerprint must be a SHA-256 note fingerprint for an archive operation.")


def visible_note(client: gkeepapi.Keep, note_id: str) -> Note | List:
    note = client.get(note_id)
    if not isinstance(note, (Note, List)) or note.trashed or note.deleted:
        raise KeepError(NOTE_UNAVAILABLE)
    return note


def summary(note: Note | List, boundary: SyncBoundary) -> dict[str, Any]:
    return {
        "id": note.id,
        "title": note.title,
        "type": "checklist" if isinstance(note, List) else "note",
        "archived": note.archived,
        "pinned": note.pinned,
        "labels": boundary.labels(note),
        **boundary.timestamps(note),
    }


def attachment_counts(note: Note | List) -> dict[str, int]:
    """Media the text does not carry; unknown blob kinds count as other."""
    counts = dict.fromkeys(("images", "drawings", "audio", "other"), 0)
    for blob in note.blobs:
        if not blob.trashed and not blob.deleted:
            counts[ATTACHMENT_KINDS.get(type(blob.blob), "other")] += 1
    return counts


def read_note(note: Note | List, boundary: SyncBoundary) -> dict[str, Any]:
    result = summary(note, boundary)
    result["text"] = note.text
    result["attachments"] = attachment_counts(note)
    result["links"] = [{"url": link.url, "title": link.title} for link in note.annotations.links]
    if isinstance(note, List):
        result["items"] = [
            {
                "id": item.id,
                "text": item.text,
                "checked": item.checked,
                "parent_item_id": item.parent_item.id if item.parent_item is not None else None,
            }
            for item in note.items
            if not item.trashed and not item.deleted
        ]
    return result


def snapshot_fingerprint(note: dict[str, Any]) -> str:
    """Fixed-size evidence for the exact read-note representation, without growing worker requests."""
    data = json.dumps(note, sort_keys=True, separators=(",", ":"), ensure_ascii=True).encode("ascii")
    return hashlib.sha256(data).hexdigest()


def execute_request(operation: str, arguments: dict[str, Any], client_factory=gkeepapi.Keep) -> dict[str, Any]:
    validate_request(operation, arguments)
    email, token = read_credentials()
    client = client_factory()
    boundary = SyncBoundary(client._keep_api)
    client._keep_api = boundary
    try:
        client.authenticate(email, token)
    except gkeepapi.exception.LoginException:
        raise KeepError(AUTH_FAILED) from None

    if operation == "list_notes":
        state = arguments.get("state", "active")
        query = arguments.get("query", "").casefold()
        offset = arguments.get("offset", 0)
        limit = arguments.get("limit", 50)
        notes = [
            note
            for note in client.all()
            if isinstance(note, (Note, List))
            and not note.trashed
            and not note.deleted
            and (state == "all" or note.archived == (state == "archived"))
            and (query in note.title.casefold() or query in note.text.casefold())
        ]
        notes.sort(key=lambda note: note.id)
        page = notes[offset : offset + limit]
        next_offset = offset + len(page) if offset + len(page) < len(notes) else None
        return {
            "notes": [dict(summary(note, boundary), preview=note.text[:160]) for note in page],
            "next_offset": next_offset,
        }

    note = visible_note(client, arguments["note_id"])
    if operation == "read_note":
        return read_note(note, boundary)

    archived = operation == "archive_note"
    if note.archived == archived:
        return {"id": note.id, "archived": archived, "changed": False}
    if "expected_fingerprint" in arguments and (
        snapshot_fingerprint(read_note(note, boundary)) != arguments["expected_fingerprint"]
    ):
        raise KeepError("The note changed since its copy was verified. It was left active in Google Keep.")
    try:
        boundary.authorize_archive(note, archived)
        client.sync()
        client.sync()
        confirmed = visible_note(client, note.id)
        if confirmed.archived != archived:
            raise KeepError(UNCERTAIN_WRITE)
    except Exception:
        # Never expose backend exception text or reuse this client's pending changes.
        raise KeepError(UNCERTAIN_WRITE) from None
    return {"id": confirmed.id, "archived": archived, "changed": True}
