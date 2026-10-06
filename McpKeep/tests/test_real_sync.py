from copy import deepcopy

import gkeepapi
import pytest

from mcp_keep.backend import execute_request
from mcp_keep.errors import KeepError
from mcp_keep.sync_boundary import SyncBoundary


class GoogleFixture:
    """Synthetic server pages; all client synchronization and parsing stay real."""

    def __init__(self, archived=False, delete_initial=True, delete_confirmation=False, target_kind="note"):
        seed = gkeepapi.Keep()
        unrelated = seed.createList("Unrelated checklist", [])
        self.initial_child = unrelated.add("Removed during loading", False, 30000)
        self.confirmation_child = unrelated.add("Removed during confirmation", False, 20000)
        unrelated.add("Retained item", True, 10000)
        self.unrelated_id = unrelated.id
        if target_kind == "note":
            target = seed.createNote("Archive target", "Preserve this body")
        else:
            target = seed.createList("Archive target", [("Preserve this item", True)])
        target.archived = archived
        target.pinned = True
        target.labels.add(seed.createLabel("Personal"))
        self.target_id = target.id
        self.nodes = []
        for note in seed.all():
            self.nodes.append(note.save())
            self.nodes.extend(child.save() for child in note.children)
        for raw in self.nodes:
            raw["serverId"] = "server-" + raw["id"]
            raw["baseVersion"] = "1"
        self.original_target = deepcopy(next(node for node in self.nodes if node["id"] == target.id))
        self.labels = [label.save() for label in seed.labels()]
        self.calls = []
        self.delete_initial = delete_initial
        self.delete_confirmation = delete_confirmation
        self.confirmation_started = False
        self.fail_write = False
        self.before_upload = None

    def changes(self, api, target_version=None, nodes=None, labels=None):
        nodes = [] if nodes is None else nodes
        self.calls.append({"nodes": deepcopy(nodes), "labels": deepcopy(labels)})
        if nodes:
            if self.before_upload is not None:
                self.before_upload()
            if self.fail_write:
                raise RuntimeError("synthetic backend error")
            target = next(node for node in nodes if node["id"] == self.target_id)
            self.original_target = deepcopy(target)
            response_nodes = deepcopy(nodes)
            if self.delete_confirmation:
                response_nodes.append({"id": self.confirmation_child.id})
                self.confirmation_started = True
            return {"nodes": response_nodes, "toVersion": str(len(self.calls)), "truncated": self.delete_confirmation}
        if len(self.calls) == 1:
            return {
                "nodes": deepcopy(self.nodes),
                "userInfo": {"labels": deepcopy(self.labels)},
                "toVersion": "1",
                "truncated": self.delete_initial,
            }
        if len(self.calls) == 2 and self.delete_initial:
            return {"nodes": [{"id": self.initial_child.id}], "toVersion": "2", "truncated": True}
        return {"nodes": [], "toVersion": str(len(self.calls)), "truncated": False}


def bind_google(monkeypatch, fixture):
    monkeypatch.setattr("mcp_keep.backend.read_credentials", lambda: ("fixture@example.com", "synthetic-token"))
    monkeypatch.setattr(gkeepapi.APIAuth, "load", lambda *args, **kwargs: True)
    monkeypatch.setattr(gkeepapi.KeepAPI, "changes", lambda api, **kwargs: fixture.changes(api, **kwargs))

    def blocked_network(*args, **kwargs):
        pytest.fail("Real-sync regression tests must not contact Google")

    monkeypatch.setattr("socket.socket.connect", blocked_network)
    monkeypatch.setattr("socket.socket.connect_ex", blocked_network)


@pytest.mark.parametrize("operation", ["list_notes", "read_note"])
def test_real_sync_reads_never_upload_remote_hydration_edits(monkeypatch, operation):
    fixture = GoogleFixture()
    bind_google(monkeypatch, fixture)
    arguments = {} if operation == "list_notes" else {"note_id": fixture.unrelated_id}
    result = execute_request(operation, arguments)
    assert len(fixture.calls) == 3
    assert all(not call["nodes"] and not call["labels"] for call in fixture.calls)
    if operation == "read_note":
        assert [item["text"] for item in result["items"]] == ["Removed during confirmation", "Retained item"]
        assert "updated" not in result
    else:
        assert all("updated" not in note for note in result["notes"])


@pytest.mark.parametrize("operation,initial_state", [("archive_note", False), ("unarchive_note", True)])
@pytest.mark.parametrize("delete_initial,delete_confirmation", [(True, False), (False, True), (True, True)])
def test_real_sync_mutations_upload_only_one_preserving_archive_transition(
    monkeypatch, operation, initial_state, delete_initial, delete_confirmation
):
    fixture = GoogleFixture(initial_state, delete_initial, delete_confirmation)
    original = deepcopy(fixture.original_target)
    bind_google(monkeypatch, fixture)
    result = execute_request(operation, {"note_id": fixture.target_id})
    assert result == {"id": fixture.target_id, "archived": not initial_state, "changed": True}
    uploads = [node for call in fixture.calls for node in call["nodes"]]
    assert len(uploads) == 1
    expected = deepcopy(original)
    expected["isArchived"] = not initial_state
    expected["timestamps"]["updated"] = uploads[0]["timestamps"]["updated"]
    assert uploads[0] == expected
    assert all(not call["labels"] for call in fixture.calls)
    assert fixture.confirmation_started == delete_confirmation


@pytest.mark.parametrize("operation,initial_state", [("archive_note", True), ("unarchive_note", False)])
def test_real_sync_noops_cannot_upload_incidental_edits(monkeypatch, operation, initial_state):
    fixture = GoogleFixture(archived=initial_state)
    bind_google(monkeypatch, fixture)
    assert execute_request(operation, {"note_id": fixture.target_id})["changed"] is False
    assert all(not call["nodes"] and not call["labels"] for call in fixture.calls)


def test_write_authorization_is_consumed_before_failure_and_cannot_replay(monkeypatch):
    fixture = GoogleFixture(delete_initial=False)
    bind_google(monkeypatch, fixture)
    client = gkeepapi.Keep()
    boundary = SyncBoundary(client._keep_api)
    client._keep_api = boundary
    client.authenticate("fixture@example.com", "synthetic-token")
    note = client.get(fixture.target_id)
    boundary.authorize_archive(note, True)
    fixture.fail_write = True
    with pytest.raises(RuntimeError):
        client.sync()
    fixture.fail_write = False
    client.sync()
    assert sum(len(call["nodes"]) for call in fixture.calls) == 1
    assert fixture.calls[-1]["nodes"] == []
    with pytest.raises(KeepError, match="already used"):
        boundary.authorize_archive(note, True)


def test_archive_guard_preserves_server_fields_despite_sdk_setter_side_effects(monkeypatch):
    fixture = GoogleFixture(delete_initial=False)
    bind_google(monkeypatch, fixture)
    client = gkeepapi.Keep()
    boundary = SyncBoundary(client._keep_api)
    client._keep_api = boundary
    client.authenticate("fixture@example.com", "synthetic-token")
    original_setter = gkeepapi.node.TopLevelNode.archived.fset

    def unsafe_setter(note, archived):
        original_setter(note, archived)
        note.pinned = False

    monkeypatch.setattr(
        gkeepapi.node.TopLevelNode,
        "archived",
        property(gkeepapi.node.TopLevelNode.archived.fget, unsafe_setter),
    )
    boundary.authorize_archive(client.get(fixture.target_id), True)
    client.sync()
    uploads = [node for call in fixture.calls for node in call["nodes"]]
    assert len(uploads) == 1
    assert uploads[0]["isPinned"] is True


def test_boundary_discards_all_unrequested_node_and_label_suggestions(monkeypatch):
    fixture = GoogleFixture(delete_initial=False)
    bind_google(monkeypatch, fixture)
    boundary = SyncBoundary(gkeepapi.KeepAPI())
    boundary.changes(nodes=fixture.nodes, labels=fixture.labels)
    assert fixture.calls[0] == {"nodes": [], "labels": None}


@pytest.mark.parametrize("operation,initial_state", [("archive_note", False), ("unarchive_note", True)])
@pytest.mark.parametrize("target_kind", ["note", "list"])
def test_mutations_preserve_removed_associations_and_opaque_server_metadata(
    monkeypatch, operation, initial_state, target_kind
):
    fixture = GoogleFixture(archived=initial_state, delete_initial=False, target_kind=target_kind)
    raw = next(node for node in fixture.nodes if node["id"] == fixture.target_id)
    raw["labelIds"][0]["deleted"] = "2026-10-05T12:00:00.000000Z"
    extra = gkeepapi.Keep().createLabel("Retained")
    fixture.labels.append(extra.save())
    raw["labelIds"].append({"labelId": extra.id, "deleted": "1970-01-01T00:00:00.000000Z"})
    raw["opaqueServerMetadata"] = {"value": [3.5, None, "preserve"]}
    original = deepcopy(raw)
    bind_google(monkeypatch, fixture)
    result = execute_request(operation, {"note_id": fixture.target_id})
    assert result["changed"] is True
    uploads = [node for call in fixture.calls for node in call["nodes"]]
    assert len(uploads) == 1
    expected = deepcopy(original)
    expected["isArchived"] = not initial_state
    expected["timestamps"]["updated"] = uploads[0]["timestamps"]["updated"]
    assert uploads[0] == expected


@pytest.mark.parametrize("operation", ["list_notes", "read_note"])
def test_read_results_exclude_removed_association_with_existing_account_definition(monkeypatch, operation):
    fixture = GoogleFixture(delete_initial=False)
    raw = next(node for node in fixture.nodes if node["id"] == fixture.target_id)
    raw["labelIds"][0]["deleted"] = "2026-10-05T12:00:00.000000Z"
    bind_google(monkeypatch, fixture)
    arguments = {} if operation == "list_notes" else {"note_id": fixture.target_id}
    result = execute_request(operation, arguments)
    if operation == "list_notes":
        result = next(note for note in result["notes"] if note["id"] == fixture.target_id)
    assert result["labels"] == []
    assert all(not call["nodes"] and not call["labels"] for call in fixture.calls)


def test_missing_or_invalidated_server_baseline_cannot_authorize_a_write():
    client = gkeepapi.Keep()
    note = client.createNote("Local only", "Must not be uploaded")
    boundary = SyncBoundary(client._keep_api)
    with pytest.raises(KeepError, match="metadata is unavailable"):
        boundary.authorize_archive(note, True)
    boundary = SyncBoundary(client._keep_api)
    boundary.observe({"nodes": [note.save()]})
    boundary.observe({"nodes": [{"id": note.id}]})
    with pytest.raises(KeepError, match="metadata is unavailable"):
        boundary.authorize_archive(note, True)
