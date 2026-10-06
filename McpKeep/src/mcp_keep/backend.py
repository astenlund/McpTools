"""Four Google Keep operations on a single disposable authenticated client."""

from typing import Any

import gkeepapi
from gkeepapi.node import List, Note

from mcp_keep.config import read_credentials
from mcp_keep.errors import AUTH_FAILED, UNCERTAIN_WRITE, KeepError
from mcp_keep.sync_boundary import SyncBoundary

ARCHIVE_STATES = ("active", "archived", "all")
OPERATIONS = ("list_notes", "read_note", "archive_note", "unarchive_note")


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


def visible_note(client: gkeepapi.Keep, note_id: str) -> Note | List:
    note = client.get(note_id)
    if not isinstance(note, (Note, List)) or note.trashed or note.deleted:
        raise KeepError("The note is unavailable. Use list_notes to obtain an active or archived note ID.")
    return note


def summary(note: Note | List, boundary: SyncBoundary) -> dict[str, Any]:
    return {
        "id": note.id,
        "title": note.title,
        "type": "checklist" if isinstance(note, List) else "note",
        "archived": note.archived,
        "pinned": note.pinned,
        "labels": boundary.labels(note),
    }


def read_note(note: Note | List, boundary: SyncBoundary) -> dict[str, Any]:
    result = summary(note, boundary)
    result["text"] = note.text
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
