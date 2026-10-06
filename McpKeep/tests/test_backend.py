from copy import deepcopy

import pytest
from support import FixtureKeep, load_state, write_state

from mcp_keep.backend import execute_request
from mcp_keep.errors import KeepError


def call(operation, **arguments):
    return execute_request(operation, arguments, FixtureKeep)


def test_list_archive_filters_and_empty_pages(keep_fixture):
    _, ids, _ = keep_fixture
    active = call("list_notes")
    assert {note["id"] for note in active["notes"]} == {ids["active"], ids["checklist"]}
    archived = call("list_notes", state="archived")
    assert [note["id"] for note in archived["notes"]] == [ids["archived"]]
    assert len(call("list_notes", state="all")["notes"]) == 3
    assert call("list_notes", query="not present") == {"notes": [], "next_offset": None}
    assert call("list_notes", offset=999) == {"notes": [], "next_offset": None}


def test_case_insensitive_filter_and_pagination(keep_fixture):
    _, ids, _ = keep_fixture
    assert call("list_notes", query="milk")["notes"][0]["id"] == ids["active"]
    assert call("list_notes", query="INBOX")["notes"][0]["id"] == ids["active"]
    collected = []
    offset = 0
    while offset is not None:
        page = call("list_notes", state="all", limit=1, offset=offset)
        collected.extend(note["id"] for note in page["notes"])
        offset = page["next_offset"]
    assert collected == sorted([ids["active"], ids["archived"], ids["checklist"]])


def test_read_text_labels_and_nested_checklist(keep_fixture):
    _, ids, _ = keep_fixture
    note = call("read_note", note_id=ids["active"])
    assert note["text"] == "Buy Milk\nPlan a trip"
    assert note["labels"] == ["Personal"]
    assert note["pinned"] is True
    checklist = call("read_note", note_id=ids["checklist"])
    assert [item["text"] for item in checklist["items"]] == ["Fruit", "Apples", "Coffee"]
    assert checklist["items"][1]["checked"] is True
    assert checklist["items"][1]["parent_item_id"] == ids["parent"]


@pytest.mark.parametrize("kind", ["trashed", "deleted", "child", "unknown"])
@pytest.mark.parametrize("operation", ["read_note", "archive_note", "unarchive_note"])
def test_unavailable_note_ids_cannot_be_read_or_mutated(keep_fixture, kind, operation):
    path, ids, _ = keep_fixture
    before = path.read_bytes()
    with pytest.raises(KeepError, match="unavailable"):
        call(operation, note_id=ids.get(kind, "missing-id"))
    assert path.read_bytes() == before


@pytest.mark.parametrize("kind", ["active", "checklist"])
def test_archive_round_trip_preserves_unrelated_content(keep_fixture, kind):
    path, ids, _ = keep_fixture
    before = call("read_note", note_id=ids[kind])
    other_before = call("read_note", note_id=ids["archived"])
    result = call("archive_note", note_id=ids[kind])
    assert result == {"id": ids[kind], "archived": True, "changed": True}
    assert call("read_note", note_id=ids[kind])["archived"] is True
    after_archive = deepcopy(load_state(path))
    assert call("archive_note", note_id=ids[kind])["changed"] is False
    assert load_state(path) == after_archive
    assert call("unarchive_note", note_id=ids[kind])["changed"] is True
    after = call("read_note", note_id=ids[kind])
    assert after == before
    assert call("read_note", note_id=ids["archived"]) == other_before
    before_noop = path.read_bytes()
    assert call("unarchive_note", note_id=ids[kind])["changed"] is False
    assert path.read_bytes() == before_noop


@pytest.mark.parametrize(
    "arguments",
    [
        {"state": "trash"},
        {"limit": 0},
        {"limit": 201},
        {"limit": 3.5},
        {"limit": True},
        {"offset": -1},
        {"offset": 2.5},
        {"query": None},
    ],
)
def test_bad_list_arguments_fail_before_authentication(arguments):
    def unexpected_client():
        pytest.fail("Invalid arguments must not construct a Google client")

    with pytest.raises(KeepError):
        execute_request("list_notes", arguments, unexpected_client)


def test_failed_write_does_not_get_uploaded_by_later_read(keep_fixture):
    path, ids, _ = keep_fixture
    state = load_state(path)
    state["failure"] = "write"
    write_state(path, state)
    with pytest.raises(KeepError, match="may have reached Google") as failure:
        call("archive_note", note_id=ids["active"])
    assert "secret-master-token" not in str(failure.value)
    state = load_state(path)
    assert state["syncs"] == 0
    state["failure"] = None
    write_state(path, state)
    assert call("read_note", note_id=ids["active"])["archived"] is False
    assert load_state(path)["syncs"] == 0


def test_authentication_error_is_safe(keep_fixture):
    path, _, _ = keep_fixture
    state = load_state(path)
    state["failure"] = "auth"
    write_state(path, state)
    with pytest.raises(KeepError, match="authentication failed") as failure:
        call("list_notes")
    assert "secret-master-token" not in str(failure.value)


def test_unconfirmed_write_reports_uncertainty(keep_fixture):
    _, ids, _ = keep_fixture

    class RevertedKeep(FixtureKeep):
        def sync(self, **kwargs):
            self.get(ids["active"]).archived = False

    with pytest.raises(KeepError, match="may have reached Google"):
        execute_request("archive_note", {"note_id": ids["active"]}, RevertedKeep)
