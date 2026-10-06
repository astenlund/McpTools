"""Constrain gkeepapi synchronization to reads and one explicit archive transition."""

import gkeepapi
from gkeepapi.node import List, Note

from mcp_keep.errors import KeepError
from mcp_keep.server_snapshot import ServerSnapshots
from mcp_keep.wire_transport import NoteWireTransport


class SyncBoundary:
    """A disposable client's sync transport starts with no write authorization.

    Server responses are captured before hydration. One archive payload may be
    prepared from that baseline and is consumed before its exchange. Every other
    exchange discards incidental SDK node and label edits. A transport guard
    separately prevents retries below this boundary.
    """

    def __init__(self, api: gkeepapi.KeepAPI) -> None:
        self._api = api
        self._pending_write: dict | None = None
        self._authorized = False
        self._snapshots = ServerSnapshots()
        self._wire = NoteWireTransport(api._send)
        api._send = self._wire.send

    def getAuth(self):
        return self._api.getAuth()

    def setAuth(self, auth) -> None:
        self._api.setAuth(auth)

    def authorize_archive(self, note: Note | List, archived: bool) -> None:
        if self._authorized:
            raise KeepError("This Google Keep client has already used its archive authorization.")
        self._authorized = True
        payload = self._snapshots.archive_payload(note, archived)
        note.archived = archived
        self._pending_write = payload

    def observe(self, response: dict) -> None:
        self._snapshots.observe(response)

    def labels(self, note: Note | List) -> list[str]:
        return self._snapshots.labels(note)

    def changes(self, target_version=None, nodes=None, labels=None):
        payload = self._pending_write
        self._pending_write = None
        with self._wire.request(payload):
            response = self._api.changes(
                target_version=target_version, nodes=[] if payload is None else [payload], labels=None
            )
        self.observe(response)
        return response
