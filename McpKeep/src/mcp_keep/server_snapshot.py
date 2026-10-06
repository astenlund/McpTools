"""Original server metadata retained before gkeepapi's lossy hydration."""

from copy import deepcopy
from datetime import datetime, timezone

from gkeepapi.node import List, NodeTimestamps, Note

from mcp_keep.errors import KeepError


class ServerSnapshots:
    """Metadata belongs to one disposable client and is refreshed by server pages.

    Tombstones invalidate individual records; full resync invalidates the whole
    set. Missing or malformed records cannot authorize writes. No state survives
    the worker, and returned copies cannot alter the captured server baseline.
    """

    def __init__(self) -> None:
        self._notes: dict[str, dict] = {}

    def observe(self, response: dict) -> None:
        if not isinstance(response, dict):
            raise KeepError("Google Keep returned unusable note metadata.")
        if response.get("forceFullResync"):
            self._notes.clear()
        nodes = response.get("nodes", [])
        if not isinstance(nodes, list):
            raise KeepError("Google Keep returned unusable note metadata.")
        for raw in nodes:
            if not isinstance(raw, dict) or not isinstance(raw.get("id"), str):
                raise KeepError("Google Keep returned unusable note metadata.")
            note_id = raw["id"]
            if "parentId" not in raw:
                self._notes.pop(note_id, None)
            elif raw.get("type") in ("NOTE", "LIST"):
                self._notes[note_id] = deepcopy(raw)
            elif note_id in self._notes:
                # A partial or unexpected replacement cannot supply a safe baseline.
                self._notes.pop(note_id, None)

    def metadata(self, note_id: str) -> dict:
        raw = self._notes.get(note_id)
        if raw is None or type(raw.get("isArchived", False)) is not bool:
            raise KeepError("Current server metadata is unavailable. Read the note again before retrying.")
        return deepcopy(raw)

    def archive_payload(self, note: Note | List, archived: bool) -> dict:
        payload = self.metadata(note.id)
        timestamps = payload.get("timestamps")
        if not isinstance(timestamps, dict):
            raise KeepError("The note's original metadata cannot be preserved safely.")
        payload["isArchived"] = archived
        timestamps["updated"] = NodeTimestamps.dt_to_str(datetime.now(tz=timezone.utc))
        return payload

    def labels(self, note: Note | List) -> list[str]:
        entries = self.metadata(note.id).get("labelIds", [])
        if not isinstance(entries, list):
            raise KeepError("Google Keep returned unusable label metadata.")
        names = {label.id: label.name for label in note.labels.all()}
        seen = set()
        result = []
        for entry in entries:
            if not isinstance(entry, dict) or not isinstance(entry.get("labelId"), str):
                raise KeepError("Google Keep returned unusable label metadata.")
            label_id = entry["labelId"]
            if label_id in seen:
                raise KeepError("Google Keep returned duplicate label associations.")
            seen.add(label_id)
            try:
                deleted = NodeTimestamps.str_to_dt(entry.get("deleted"))
            except (TypeError, ValueError):
                raise KeepError("Google Keep returned unusable label metadata.") from None
            if deleted <= NodeTimestamps.int_to_dt(0):
                name = names.get(label_id)
                if not isinstance(name, str):
                    raise KeepError("An active label's name is unavailable. Retry after refreshing the account.")
                result.append(name)
        return result
