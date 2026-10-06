from copy import deepcopy
from types import SimpleNamespace

import gkeepapi
import pytest
from test_real_sync import GoogleFixture

from mcp_keep.backend import execute_request
from mcp_keep.errors import KeepError
from mcp_keep.wire_transport import NoteWireTransport


@pytest.mark.parametrize("operation,initial_state", [("archive_note", False), ("unarchive_note", True)])
@pytest.mark.parametrize("error_code", [401, 429])
def test_actual_sdk_retry_loop_cannot_resubmit_archive_write(monkeypatch, operation, initial_state, error_code):
    fixture = GoogleFixture(archived=initial_state, delete_initial=False)
    writes = []

    def send(api, **request):
        body = request["json"]
        if body["nodes"]:
            writes.append(deepcopy(body["nodes"]))
            assert request["allow_redirects"] is False
            return SimpleNamespace(json=lambda: {"error": {"code": error_code}})
        response = fixture.changes(api, target_version=body.get("targetVersion"), nodes=body["nodes"])
        return SimpleNamespace(json=lambda: response)

    monkeypatch.setattr("mcp_keep.backend.read_credentials", lambda: ("fixture@example.com", "synthetic-token"))
    monkeypatch.setattr(gkeepapi.APIAuth, "load", lambda *args, **kwargs: True)
    monkeypatch.setattr(gkeepapi.APIAuth, "refresh", lambda *args, **kwargs: None)
    monkeypatch.setattr(gkeepapi.API, "_send", send)
    monkeypatch.setattr(gkeepapi.time, "sleep", lambda seconds: None)
    monkeypatch.setattr("socket.socket.connect", lambda *args, **kwargs: pytest.fail("Unexpected network request"))
    with pytest.raises(KeepError, match="may have reached Google"):
        execute_request(operation, {"note_id": fixture.target_id})
    assert len(writes) == 1


def test_read_retry_remains_receive_only_and_can_complete(monkeypatch):
    fixture = GoogleFixture(delete_initial=False)
    calls = []

    def send(api, **request):
        body = request["json"]
        calls.append(deepcopy(body["nodes"]))
        if len(calls) == 1:
            return SimpleNamespace(json=lambda: {"error": {"code": 429}})
        return SimpleNamespace(json=lambda: fixture.changes(api, nodes=body["nodes"]))

    monkeypatch.setattr("mcp_keep.backend.read_credentials", lambda: ("fixture@example.com", "synthetic-token"))
    monkeypatch.setattr(gkeepapi.APIAuth, "load", lambda *args, **kwargs: True)
    monkeypatch.setattr(gkeepapi.API, "_send", send)
    monkeypatch.setattr(gkeepapi.time, "sleep", lambda seconds: None)
    assert execute_request("list_notes", {})["notes"]
    assert calls == [[], []]


def test_transport_rejects_unscoped_and_unapproved_updates():
    sent = []
    transport = NoteWireTransport(lambda **request: sent.append(request))
    with pytest.raises(KeepError, match="unapproved"):
        transport.send(json={"nodes": []})
    with transport.request(None):
        with pytest.raises(KeepError, match="unapproved"):
            transport.send(json={"nodes": [{"id": "unrequested"}]})
        with pytest.raises(KeepError, match="unapproved"):
            transport.send(json={"nodes": [], "userInfo": {"labels": [{"id": "unrequested"}]}})
    assert sent == []


def test_transport_clears_context_after_send_failure_and_keeps_write_consumed():
    payload = {"id": "target", "isArchived": True}
    attempts = []

    def fail(**request):
        attempts.append(request)
        raise OSError("synthetic failure")

    transport = NoteWireTransport(fail)
    with pytest.raises(OSError), transport.request(payload):
        transport.send(json={"nodes": [payload]})
    with pytest.raises(KeepError, match="unapproved"):
        transport.send(json={"nodes": []})
    with transport.request(payload), pytest.raises(KeepError, match="will not be sent again"):
        transport.send(json={"nodes": [payload]})
    assert len(attempts) == 1
