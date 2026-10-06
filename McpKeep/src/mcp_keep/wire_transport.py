"""Restrict actual HTTP attempts beneath gkeepapi's internal retry loop."""

from contextlib import contextmanager
from copy import deepcopy

from mcp_keep.errors import KeepError


class NoteWireTransport:
    """Only an active sync exchange can send, and a client gets one write attempt.

    Request context always clears on success or failure. The write-attempt bit
    never clears, so SDK retry paths cannot reuse the permission. Reads can retry
    with empty update lists. Mutation redirects are disabled at Requests' layer.
    """

    def __init__(self, send) -> None:
        self._send = send
        self._active = False
        self._payload: dict | None = None
        self._write_attempted = False

    @contextmanager
    def request(self, payload: dict | None):
        if self._active:
            raise KeepError("Overlapping Google Keep sync exchanges are not supported.")
        self._active = True
        self._payload = deepcopy(payload)
        try:
            yield
        finally:
            self._active = False
            self._payload = None

    def send(self, **request):
        body = request.get("json")
        if not self._active or not isinstance(body, dict):
            raise KeepError("An unapproved Google Keep request was blocked.")
        nodes = body.get("nodes")
        expected = [] if self._payload is None else [self._payload]
        user_info = body.get("userInfo", {})
        if nodes != expected or not isinstance(user_info, dict) or user_info.get("labels"):
            raise KeepError("An unapproved Google Keep update was blocked.")
        if self._payload is not None:
            if self._write_attempted:
                raise KeepError("The failed archive request will not be sent again.")
            self._write_attempted = True
            request["allow_redirects"] = False
        return self._send(**request)
