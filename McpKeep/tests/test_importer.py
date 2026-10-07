import json
import sys
from copy import deepcopy
from dataclasses import replace
from datetime import datetime, timedelta, timezone
from pathlib import Path

import pytest
from support import FixtureKeep, clean_client, load_state, write_state

from mcp_keep.backend import execute_request, snapshot_fingerprint
from mcp_keep.errors import NOTE_UNAVAILABLE, REQUEST_FAILED, UNCERTAIN_WRITE, KeepError
from mcp_keep.gateway import KeepGateway
from mcp_keep.importer import (
    COMPLETED,
    LEDGER_DIR,
    LEDGER_FILE,
    ImportBusy,
    ImportFailure,
    Settings,
    _exclusive,
    _yaml_scalar,
    label_tags,
    main,
    run,
)

NOW = datetime(2026, 10, 7, 4, 0, tzinfo=timezone(timedelta(hours=2)))
NO_MEDIA = {"images": 0, "drawings": 0, "audio": 0, "other": 0}
SUMMARY_FIELDS = ("id", "title", "type", "archived", "pinned", "labels", "created", "edited")
BOX, CHECKED_BOX, A_RING = chr(0x2610), chr(0x2611), chr(0x00C5)


def make_note(keep_id, title="", text="", created="2020-01-01T00:00:00.000Z", **fields):
    note = {
        "id": keep_id,
        "title": title,
        "type": "note",
        "archived": False,
        "pinned": False,
        "labels": [],
        "created": created,
        "edited": created,
        "text": text,
        "attachments": dict(NO_MEDIA),
        "links": [],
    }
    note.update(fields)
    return note


class FakeKeep:
    """In-memory stand-in for KeepGateway with the same result shapes."""

    def __init__(self, *notes):
        self.notes = {note["id"]: note for note in notes}
        self.archive_calls = []
        self.fail_archive = False

    def call(self, operation, **arguments):
        if operation == "list_notes":
            assert arguments["state"] == "active"
            matching = sorted((note for note in self.notes.values() if not note["archived"]), key=lambda n: n["id"])
            offset, limit = arguments["offset"], arguments["limit"]
            page = matching[offset : offset + limit]
            more = offset + len(page) < len(matching)
            return {
                "notes": [{field: note[field] for field in SUMMARY_FIELDS} for note in page],
                "next_offset": offset + len(page) if more else None,
            }
        if operation == "read_note":
            if arguments["note_id"] not in self.notes:
                raise KeepError(NOTE_UNAVAILABLE)
            return deepcopy(self.notes[arguments["note_id"]])
        assert operation == "archive_note"
        current = snapshot_fingerprint(self.notes[arguments["note_id"]])
        if current != arguments.get("expected_fingerprint", current):
            raise KeepError("The note changed since its copy was verified.")
        self.archive_calls.append(arguments["note_id"])
        if self.fail_archive:
            raise KeepError(UNCERTAIN_WRITE)
        self.notes[arguments["note_id"]]["archived"] = True
        return {"id": arguments["note_id"], "archived": True, "changed": True}


@pytest.fixture
def settings(tmp_path):
    vault = tmp_path / "vault"
    vault.mkdir()
    return Settings(vault=vault, lock_file=tmp_path / "import.lock")


def inbox_files(settings):
    inbox = settings.vault / settings.inbox
    return sorted(path.name for path in inbox.glob("*.md")) if inbox.is_dir() else []


def ledger_lines(settings):
    path = settings.vault / LEDGER_DIR / LEDGER_FILE
    return path.read_bytes().decode("ascii").splitlines() if path.exists() else []


def test_imports_oldest_eligible_note_first_then_archives_it(settings):
    keep = FakeKeep(
        make_note("a", "Newer", "Second", created="2021-01-01T00:00:00.000Z"),
        make_note("b", "Older", "First line\nSecond line", created="2020-05-04T03:02:01.123Z"),
        make_note("c", "Pinned", created="2019-01-01T00:00:00.000Z", pinned=True),
        make_note("d", "Labelled", created="2018-01-01T00:00:00.000Z", labels=["Keep"]),
    )
    assert run(settings, keep, NOW) == []
    assert inbox_files(settings) == ["Older.md"]
    assert (settings.vault / "Inbox" / "Older.md").read_bytes() == (
        b"---\n"
        b'keep_id: "b"\n'
        b'keep_title: "Older"\n'
        b'keep_created: "2020-05-04T03:02:01.123Z"\n'
        b'keep_edited: "2020-05-04T03:02:01.123Z"\n'
        b'imported: "2026-10-07T04:00:00+02:00"\n'
        b"---\n"
        b"First line\n"
        b"Second line\n"
    )
    assert keep.archive_calls == ["b"]
    assert [json.loads(line)["status"] for line in ledger_lines(settings)] == ["imported", COMPLETED]
    assert run(settings, keep, NOW) == []
    assert inbox_files(settings) == ["Newer.md", "Older.md"]
    assert run(settings, keep, NOW) == []
    assert keep.archive_calls == ["b", "a"]
    assert not keep.notes["c"]["archived"] and not keep.notes["d"]["archived"]


def test_checklist_renders_nested_tasks_and_is_named_from_its_first_item(settings):
    items = [
        {"id": "i1", "text": "Fruit", "checked": False, "parent_item_id": None},
        {"id": "i2", "text": "Apples", "checked": True, "parent_item_id": "i1"},
        {"id": "i3", "text": "Coffee", "checked": False, "parent_item_id": None},
    ]
    generated_text = f"{BOX} Fruit\n  {CHECKED_BOX} Apples\n{BOX} Coffee"
    keep = FakeKeep(make_note("l", "", generated_text, type="checklist", items=items))
    run(settings, keep, NOW)
    content = (settings.vault / "Inbox" / "Fruit.md").read_text(encoding="utf-8")
    assert content.endswith("---\n- [ ] Fruit\n\t- [x] Apples\n- [ ] Coffee\n")
    assert "keep_title" not in content


def test_note_with_media_is_imported_held_and_reported_once(settings):
    media = {"images": 2, "drawings": 1, "audio": 0, "other": 0}
    keep = FakeKeep(make_note("m", "Trip", "Photos below", attachments=media))
    report = run(settings, keep, NOW)
    assert len(report) == 1
    assert '"Trip"' in report[0] and "2 images and 1 drawing" in report[0]
    content = (settings.vault / "Inbox" / "Trip.md").read_text(encoding="utf-8")
    assert 'keep_not_copied: "2 images and 1 drawing"' in content
    assert "> [!warning] Not fully imported" in content
    assert keep.archive_calls == []
    assert run(settings, keep, NOW) == []
    assert inbox_files(settings) == ["Trip.md"]
    assert keep.archive_calls == []


def test_uncertain_archive_is_retried_before_new_imports_without_rewriting(settings):
    keep = FakeKeep(make_note("b", "Older", "Body"))
    keep.fail_archive = True
    with pytest.raises(ImportFailure, match="was imported, but archiving it failed"):
        run(settings, keep, NOW)
    written = settings.vault / "Inbox" / "Older.md"
    before = (written.read_bytes(), written.stat().st_mtime_ns)
    keep.fail_archive = False
    keep.notes["n"] = make_note("n", "Newcomer", created="2019-01-01T00:00:00.000Z")
    assert run(settings, keep, NOW) == []
    assert keep.archive_calls == ["b", "b"]
    assert (written.read_bytes(), written.stat().st_mtime_ns) == before
    assert inbox_files(settings) == ["Older.md"]
    run(settings, keep, NOW)
    assert inbox_files(settings) == ["Newcomer.md", "Older.md"]


def test_file_written_before_a_crash_is_reused_instead_of_duplicated(settings):
    keep = FakeKeep(make_note("b", "Older", "Body"))
    keep.fail_archive = True
    with pytest.raises(ImportFailure):
        run(settings, keep, NOW)
    (settings.vault / LEDGER_DIR / LEDGER_FILE).unlink()
    keep.fail_archive = False
    written = settings.vault / "Inbox" / "Older.md"
    before = (written.read_bytes(), written.stat().st_mtime_ns)
    run(settings, keep, NOW)
    assert inbox_files(settings) == ["Older.md"]
    assert (written.read_bytes(), written.stat().st_mtime_ns) == before


@pytest.mark.parametrize(
    "unrelated",
    [
        'Personal instructions\nExample:\nkeep_id: "b"\nValuable text\n',
        '---\ntitle: "Example"\n---\nkeep_id: "b"\nValuable text\n',
        "---\nimported: 2026-10-01\n---\nValuable text\n",
        "---\nimported: true\n---\nValuable text\n",
        "---\nimported: [invalid, json]\n---\nValuable text\n",
        "---\nimported: 2026-10-01\nimported: 2026-10-02\n---\nValuable text\n",
    ],
)
def test_recovery_does_not_treat_body_text_as_provenance(settings, unrelated):
    inbox = settings.vault / settings.inbox
    inbox.mkdir()
    existing = inbox / "Existing.md"
    original = unrelated.encode("utf-8")
    existing.write_bytes(original)
    keep = FakeKeep(make_note("b", "New note", "Imported text"))

    run(settings, keep, NOW)

    assert existing.read_bytes() == original
    assert inbox_files(settings) == ["Existing.md", "New note.md"]
    assert keep.notes["b"]["archived"]


@pytest.mark.parametrize("ambiguity", ["duplicate_file", "duplicate_key", "incomplete_frontmatter"])
def test_ambiguous_or_incomplete_provenance_stops_without_overwriting(settings, ambiguity):
    from mcp_keep.importer import render

    inbox = settings.vault / settings.inbox
    inbox.mkdir()
    note = make_note("b", "Older", "Body")
    content = render(note, None, NOW)
    if ambiguity == "duplicate_key":
        content = content.replace('keep_id: "b"', 'keep_id: "b"\nkeep_id: "b"')
    elif ambiguity == "incomplete_frontmatter":
        content = '---\nkeep_id: "b"\n'
    existing = inbox / "Existing.md"
    existing.write_bytes(content.encode("utf-8"))
    if ambiguity == "duplicate_file":
        (inbox / "Other.md").write_bytes(existing.read_bytes())
    before = {path.name: path.read_bytes() for path in inbox.iterdir()}
    keep = FakeKeep(note)

    with pytest.raises(ImportFailure):
        run(settings, keep, NOW)

    assert {path.name: path.read_bytes() for path in inbox.iterdir()} == before
    assert keep.archive_calls == []
    assert ledger_lines(settings) == []


@pytest.mark.parametrize("change", ["text", "media", "missing_copy", "edited_copy", "missing_note"])
def test_archive_recovery_rechecks_current_note_and_copy(settings, change):
    keep = FakeKeep(make_note("b", "Older", "Body"))
    keep.fail_archive = True
    with pytest.raises(ImportFailure):
        run(settings, keep, NOW)
    written = settings.vault / settings.inbox / "Older.md"
    original = written.read_bytes()
    if change == "text":
        keep.notes["b"]["text"] = "Changed after the failed archive"
    elif change == "media":
        keep.notes["b"]["attachments"]["images"] = 1
    elif change == "missing_copy":
        written.unlink()
    elif change == "edited_copy":
        written.write_bytes(original + b"A local edit\n")
    else:
        del keep.notes["b"]
    keep.fail_archive = False
    keep.notes["n"] = make_note("n", "Newcomer")
    before = written.read_bytes() if written.exists() else None

    with pytest.raises(ImportFailure) as failure:
        run(settings, keep, NOW)

    assert "Inbox/Older.md" in str(failure.value)
    assert str(failure.value).endswith("Until then, every run stops here and imports nothing new.")
    assert keep.archive_calls == ["b"]
    assert (written.read_bytes() if written.exists() else None) == before
    assert not (settings.vault / settings.inbox / "Newcomer.md").exists()
    if change != "missing_note":
        assert not keep.notes["b"]["archived"]


def test_completed_import_is_not_rearchived_after_manual_restore(settings):
    keep = FakeKeep(make_note("b", "Older", "Body"))
    run(settings, keep, NOW)
    assert json.loads(ledger_lines(settings)[-1])["status"] == COMPLETED
    keep.notes["b"]["archived"] = False
    keep.notes["b"]["text"] = "A deliberate new edit"
    (settings.vault / settings.inbox / "Older.md").unlink()
    keep.notes["n"] = make_note("n", "Newcomer")

    run(settings, keep, NOW)

    assert keep.archive_calls == ["b", "n"]
    assert not keep.notes["b"]["archived"]
    assert inbox_files(settings) == ["Newcomer.md"]


def test_confirmed_archive_survives_interruption_before_completion_record(settings, monkeypatch):
    from mcp_keep.importer import Ledger

    keep = FakeKeep(make_note("b", "Older", "Body"))
    complete = Ledger.complete

    def interrupted_complete(self, keep_id):
        raise OSError("Interrupted before recording completion")

    monkeypatch.setattr(Ledger, "complete", interrupted_complete)
    with pytest.raises(OSError):
        run(settings, keep, NOW)
    assert keep.notes["b"]["archived"]
    assert json.loads(ledger_lines(settings)[-1])["status"] == "imported"
    monkeypatch.setattr(Ledger, "complete", complete)

    assert run(settings, keep, NOW) == []

    assert keep.archive_calls == ["b"]
    assert json.loads(ledger_lines(settings)[-1])["status"] == COMPLETED


@pytest.mark.parametrize("skip", ["pinned", "label"])
def test_skipped_pending_import_does_not_block_new_notes(settings, skip):
    keep = FakeKeep(make_note("b", "Older", "Body"))
    keep.fail_archive = True
    with pytest.raises(ImportFailure):
        run(settings, keep, NOW)
    keep.fail_archive = False
    if skip == "pinned":
        keep.notes["b"]["pinned"] = True
    else:
        keep.notes["b"]["labels"] = ["KEEP"]
    keep.notes["n"] = make_note("n", "Newcomer")

    run(settings, keep, NOW)

    assert keep.archive_calls == ["b", "n"]
    assert not keep.notes["b"]["archived"]


@pytest.mark.parametrize("text", ["Body\n", "Body\n\n\n", "Body\r\n\r\n", "Body\r\r"])
def test_note_text_keeps_trailing_blank_lines(settings, text):
    keep = FakeKeep(make_note("b", "Blank lines", text))

    run(settings, keep, NOW)

    body = (settings.vault / settings.inbox / "Blank lines.md").read_bytes().split(b"---\n", 2)[2]
    assert body == text.replace("\r\n", "\n").replace("\r", "\n").encode("utf-8")


def test_new_file_collision_during_write_preserves_the_existing_file(settings, monkeypatch):
    import mcp_keep.importer as importer

    keep = FakeKeep(make_note("b", "Older", "Body"))
    link = importer.os.link

    def concurrent_file(source, target):
        target.write_bytes(b"A note created by another writer\n")
        return link(source, target)

    monkeypatch.setattr(importer.os, "link", concurrent_file)

    with pytest.raises(FileExistsError):
        run(settings, keep, NOW)

    assert (settings.vault / settings.inbox / "Older.md").read_bytes() == b"A note created by another writer\n"
    assert keep.archive_calls == []
    assert ledger_lines(settings) == []


def test_changed_unrecorded_copy_stays_untouched(settings):
    keep = FakeKeep(make_note("b", "Older", "Body"))
    keep.fail_archive = True
    with pytest.raises(ImportFailure):
        run(settings, keep, NOW)
    (settings.vault / LEDGER_DIR / LEDGER_FILE).unlink()
    written = settings.vault / settings.inbox / "Older.md"
    original = written.read_bytes() + b"A local addition\n"
    written.write_bytes(original)
    keep.fail_archive = False

    with pytest.raises(ImportFailure, match="no longer matches"):
        run(settings, keep, NOW)

    assert written.read_bytes() == original
    assert keep.archive_calls == ["b"]
    assert ledger_lines(settings) == []


@pytest.mark.parametrize(
    "frontmatter",
    [
        "keep_id: other-id\nimported: 2026-10-07T04:15:30+02:00",
        "keep_id: 'other''s id'\nimported: '2026-10-07T04:15:30+02:00'",
        "keep_id: other-id # copied from Keep\nimported: 2026-10-07T04:15:30+02:00",
        'keep_id: "other-id" # copied from Keep\nimported: "2026-10-07T04:15:30+02:00" # import time',
        "keep_id: 'other-id' # 'Keep ID'\nimported: '2026-10-07T04:15:30+02:00' # time",
    ],
)
def test_frontmatter_resaved_without_json_quotes_does_not_block_imports(settings, frontmatter):
    inbox = settings.vault / settings.inbox
    inbox.mkdir()
    existing = inbox / "Resaved.md"
    original = f"---\n{frontmatter}\n---\n- [ ] item\n".encode()
    existing.write_bytes(original)
    keep = FakeKeep(make_note("b", "New note", "Imported text"))

    run(settings, keep, NOW)

    assert existing.read_bytes() == original
    assert inbox_files(settings) == ["New note.md", "Resaved.md"]
    assert keep.notes["b"]["archived"]


@pytest.mark.parametrize("identity", [b"b", b"'b' # 'Keep ID'", b'"b" # copied from Keep'])
def test_resaved_unrecorded_copy_is_still_recognized_instead_of_duplicated(settings, identity):
    keep = FakeKeep(make_note("b", "Older", "Body"))
    keep.fail_archive = True
    with pytest.raises(ImportFailure):
        run(settings, keep, NOW)
    (settings.vault / LEDGER_DIR / LEDGER_FILE).unlink()
    written = settings.vault / settings.inbox / "Older.md"
    resaved = written.read_bytes().replace(b'keep_id: "b"', b"keep_id: " + identity)
    written.write_bytes(resaved)
    keep.fail_archive = False

    with pytest.raises(ImportFailure, match="no longer matches its Keep note"):
        run(settings, keep, NOW)

    assert inbox_files(settings) == ["Older.md"]
    assert written.read_bytes() == resaved
    assert keep.archive_calls == ["b"]
    assert not keep.notes["b"]["archived"]
    assert ledger_lines(settings) == []


@pytest.mark.parametrize(
    "text,value",
    [
        ('"14d9.b2"', "14d9.b2"),
        ('"14d9.b2" # comment', "14d9.b2"),
        ("'14d9.b2' # 'Keep ID'", "14d9.b2"),
        ("'it''s'", "it's"),
        ("'it''s' # 'owner''s note'", "it's"),
        ("14d9.b2", "14d9.b2"),
        ("  14d9.b2   # comment", "14d9.b2"),
        ("\t'14d9.b2' # comment", "14d9.b2"),
        ("a#b", "a#b"),
        ("--- other", "--- other"),
        ("... other", "... other"),
        ('"a#b" # comment', "a#b"),
        ("2026-10-07T04:15:30+02:00", "2026-10-07T04:15:30+02:00"),
        ('"id' + chr(92) + 'u0031" # comment', "id1"),
        ("'unterminated", None),
        ('"unterminated', None),
        ("'id' 'trailing'", None),
        ('"id" trailing', None),
        ("[id]", None),
        ("{id: other}", None),
        ("|", None),
        (">", None),
        ("!!int 123", None),
        ("!custom id", None),
        ("*unknown", None),
        ("id\n---\nother", None),
        ("[" * 2000 + "id" + "]" * 2000, None),
        ("", None),
    ],
)
def test_yaml_scalar_styles(text, value):
    assert _yaml_scalar(text) == value


@pytest.mark.parametrize("field", ["keep_id", "imported"])
@pytest.mark.parametrize("continuation", ["  continued", "  continued: malformed", "\n# comment\n  continued"])
def test_multiline_provenance_is_rejected_without_importing(settings, field, continuation):
    inbox = settings.vault / settings.inbox
    inbox.mkdir()
    existing = inbox / "Existing.md"
    values = {"keep_id": "other", "imported": "2026-10-07T04:00:00+02:00"}
    frontmatter = "\n".join(
        f"{key}: {value}" + ("\n" + continuation if key == field else "") for key, value in values.items()
    )
    original = ("---\n" + frontmatter + "\n---\nBody\n").encode("utf-8")
    existing.write_bytes(original)
    keep = FakeKeep(make_note("b", "New note", "Body"))

    with pytest.raises(ImportFailure, match="multiline provenance"):
        run(settings, keep, NOW)

    assert existing.read_bytes() == original
    assert inbox_files(settings) == ["Existing.md"]
    assert keep.archive_calls == []


def test_document_marker_text_does_not_claim_another_notes_identity(settings):
    inbox = settings.vault / settings.inbox
    inbox.mkdir()
    existing = inbox / "Existing.md"
    original = b"---\nkeep_id: --- b\nimported: 2026-10-07T04:00:00+02:00\n---\nOther body\n"
    existing.write_bytes(original)
    keep = FakeKeep(make_note("b", "New note", "Body"))

    run(settings, keep, NOW)

    assert existing.read_bytes() == original
    assert inbox_files(settings) == ["Existing.md", "New note.md"]
    assert keep.notes["b"]["archived"]


def test_malformed_provenance_still_requires_repair_after_manual_archive(settings):
    keep = FakeKeep(make_note("b", "Older", "Body"))
    keep.fail_archive = True
    with pytest.raises(ImportFailure):
        run(settings, keep, NOW)
    written = settings.vault / settings.inbox / "Older.md"
    malformed = written.read_bytes().replace(b'imported: "2026-10-07T04:00:00+02:00"', b'imported: "broken')
    written.write_bytes(malformed)
    keep.notes["b"]["archived"] = True
    keep.fail_archive = False
    keep.notes["n"] = make_note("n", "Newcomer")
    assert run(settings, keep, NOW) == []
    assert json.loads(ledger_lines(settings)[-1])["status"] == COMPLETED

    with pytest.raises(ImportFailure, match="unreadable imported"):
        run(settings, keep, NOW)

    assert written.read_bytes() == malformed
    assert keep.archive_calls == ["b"]
    assert not keep.notes["n"]["archived"]
    written.rename(settings.vault / "Older.md")
    assert run(settings, keep, NOW) == []
    assert keep.notes["n"]["archived"]


def test_note_gone_from_keep_names_its_copy_and_clears_with_a_completed_line(settings):
    keep = FakeKeep(make_note("b", "Older", "Body"))
    keep.fail_archive = True
    with pytest.raises(ImportFailure):
        run(settings, keep, NOW)
    del keep.notes["b"]
    keep.fail_archive = False
    keep.notes["n"] = make_note("n", "Newcomer")

    with pytest.raises(ImportFailure) as failure:
        run(settings, keep, NOW)

    message = str(failure.value)
    assert "The Keep note for Inbox/Older.md is in Keep's trash or deleted" in message
    assert '"Resolving a stuck note" in README_KEEP.md' in message
    assert "list_notes" not in message
    pending = json.loads(ledger_lines(settings)[-1])
    with (settings.vault / LEDGER_DIR / LEDGER_FILE).open("ab") as stream:
        stream.write(json.dumps({**pending, "status": COMPLETED}).encode("ascii") + b"\n")

    assert run(settings, keep, NOW) == []
    assert inbox_files(settings) == ["Newcomer.md", "Older.md"]


def test_other_keep_failures_while_finishing_an_import_stay_read_failures(settings):
    keep = FakeKeep(make_note("b", "Older", "Body"))
    keep.fail_archive = True
    with pytest.raises(ImportFailure):
        run(settings, keep, NOW)
    archive_or_list = keep.call

    def offline_reads(operation, **arguments):
        if operation == "read_note":
            raise KeepError(REQUEST_FAILED)
        return archive_or_list(operation, **arguments)

    keep.call = offline_reads

    with pytest.raises(ImportFailure, match="^Reading Google Keep failed: Google Keep request failed"):
        run(settings, keep, NOW)


@pytest.mark.parametrize("media", [False, True])
def test_large_generated_frontmatter_imports_and_does_not_block_later_notes(settings, media):
    labels = [f"Label{number}" + chr(0x65E5) * 50 for number in range(30)]
    keep = FakeKeep(make_note("a", "Metadata", "Body", labels=labels, attachments={**NO_MEDIA, "images": int(media)}))

    report = run(settings, keep, NOW)

    written = settings.vault / settings.inbox / "Metadata.md"
    assert len(written.read_bytes().split(b"---\n", 2)[1]) > 4096
    assert len(report) == int(media)
    assert keep.notes["a"]["archived"] is not media
    keep.notes["n"] = make_note("n", "Newcomer")
    assert run(settings, keep, NOW) == []
    assert keep.notes["n"]["archived"] is True


def test_large_frontmatter_recovers_without_rewriting_the_copy(settings):
    labels = [f"Label{number}" + chr(0x65E5) * 50 for number in range(30)]
    keep = FakeKeep(make_note("a", "Metadata", "Body", labels=labels))
    keep.fail_archive = True
    with pytest.raises(ImportFailure):
        run(settings, keep, NOW)
    written = settings.vault / settings.inbox / "Metadata.md"
    original = (written.read_bytes(), written.stat().st_mtime_ns)
    (settings.vault / LEDGER_DIR / LEDGER_FILE).unlink()
    keep.fail_archive = False

    assert run(settings, keep, NOW) == []

    assert (written.read_bytes(), written.stat().st_mtime_ns) == original
    assert keep.notes["a"]["archived"] is True
    assert inbox_files(settings) == ["Metadata.md"]


def test_unicode_line_separator_in_title_does_not_split_frontmatter(settings):
    keep = FakeKeep(make_note("a", "Title" + chr(0x2028) + "continuation", "Body"))

    run(settings, keep, NOW)

    assert keep.notes["a"]["archived"] is True
    assert len(inbox_files(settings)) == 1


def test_file_names_are_sanitized_and_kept_unique(settings):
    keep = FakeKeep(
        make_note("1", 'a/b: c? "d" [e]#f'),
        make_note("2", "CON"),
        make_note("3", "Plan"),
        make_note("4", "Plan"),
        make_note("5", "", "\n  ...  \nFirst words"),
        make_note("6", "", ""),
        make_note("7", "word " * 30),
    )
    run(replace(settings, limit=10), keep, NOW)
    assert inbox_files(settings) == sorted(
        [
            "a b c d e f.md",
            "CON_.md",
            "Plan.md",
            "Plan (2).md",
            "First words.md",
            "Untitled.md",
            ("word " * 16).strip() + ".md",
        ]
    )


def test_truncated_ledger_tail_is_dropped_and_corrupt_lines_fail_closed(settings):
    ledger = settings.vault / LEDGER_DIR / LEDGER_FILE
    ledger.parent.mkdir()
    valid = b'{"keep_id": "old", "status": "completed", "path": "Inbox/Old.md", "at": "x"}\n'
    ledger.write_bytes(valid + b'{"keep_id": "b", "sta')
    keep = FakeKeep(make_note("b", "Older", "Body"))
    run(settings, keep, NOW)
    lines = ledger_lines(settings)
    assert lines[0].encode("ascii") + b"\n" == valid
    assert len(lines) == 3 and '"keep_id": "b"' in lines[1]
    ledger.write_bytes(valid + b"not json\n")
    keep.notes["n"] = make_note("n", "Next")
    with pytest.raises(ImportFailure, match="Line 2 .* is unreadable"):
        run(settings, keep, NOW)
    assert inbox_files(settings) == ["Older.md"]


def test_links_missing_from_the_text_are_listed(settings):
    links = [
        {"url": "https://in.example/text", "title": "Inline"},
        {"url": "https://docs.example/a (b)", "title": "Docs [v2]"},
    ]
    keep = FakeKeep(make_note("k", "Links", "See https://in.example/text", links=links))
    run(settings, keep, NOW)
    content = (settings.vault / "Inbox" / "Links.md").read_text(encoding="utf-8")
    assert content.endswith(
        "See https://in.example/text\n\n## Links\n\n- [Docs \\[v2\\]](<https://docs.example/a (b)>)\n"
    )


def test_labels_become_obsidian_tags():
    labels = ["Shopping list", "2024", f"{A_RING}re trip", "shopping LIST", "#Work/Projects"]
    assert label_tags(labels) == ["Shopping-list", f"{A_RING}re-trip", "Work/Projects"]


def test_dry_run_changes_nothing(settings):
    keep = FakeKeep(make_note("b", "Older", "Body"))
    report = run(replace(settings, dry_run=True), keep, NOW)
    assert report[0].startswith("Would write Inbox/Older.md and archive it in Keep:\n\n---\n")
    assert list(settings.vault.iterdir()) == []
    assert keep.archive_calls == []


def test_overlapping_runs_are_refused(settings):
    with _exclusive(settings.lock_file):
        with pytest.raises(ImportBusy):
            run(settings, FakeKeep(make_note("b", "Older")), NOW)


def test_main_exit_codes(tmp_path, capsys):
    lock = ["--lock-file", str(tmp_path / "import.lock")]
    assert main(["--vault", str(tmp_path / "missing"), *lock], FakeKeep()) == 1
    assert "Keep import failed: The vault folder" in capsys.readouterr().out
    vault = tmp_path / "vault"
    vault.mkdir()
    keep = FakeKeep(make_note("m", "Trip", attachments={**NO_MEDIA, "audio": 1}))
    assert main(["--vault", str(vault), *lock], keep) == 0
    assert "1 audio clip that could not be copied" in capsys.readouterr().out
    keep.notes["b"] = make_note("b", "Older")
    keep.fail_archive = True
    assert main(["--vault", str(vault), *lock], keep) == 1
    assert "may have reached Google" in capsys.readouterr().out


def test_conditional_archive_of_long_unicode_note_through_real_worker(keep_fixture, settings):
    import gkeepapi

    path, _, _ = keep_fixture
    state = load_state(path)
    client = gkeepapi.Keep()
    client.restore(state["keep"])
    note = client.createNote("Long Unicode", chr(0x00E5) * 12000)
    clean_client(client)
    state["keep"] = client.dump()
    write_state(path, state)
    worker = Path(__file__).with_name("fixture_worker.py")
    gateway = KeepGateway([sys.executable, str(worker)])
    current = gateway.call("read_note", note_id=note.id)
    arguments = {"note_id": note.id, "expected_fingerprint": snapshot_fingerprint(current)}

    result = gateway.call("archive_note", **arguments)

    assert len(json.dumps(arguments)) < 512
    assert result["archived"] is True
    assert gateway.call("read_note", note_id=note.id)["text"] == note.text


def test_media_report_survives_a_later_archive_failure(settings, capsys):
    keep = FakeKeep(make_note("a", "Media", attachments={**NO_MEDIA, "images": 1}), make_note("b", "Failure"))
    keep.fail_archive = True
    arguments = ["--vault", str(settings.vault), "--lock-file", str(settings.lock_file), "--limit", "2"]

    assert main(arguments, keep) == 1

    output = capsys.readouterr().out
    assert 'Imported "Media"' in output
    assert "Keep import failed:" in output
    keep.fail_archive = False
    assert main(arguments, keep) == 0
    assert capsys.readouterr().out == ""
    assert not keep.notes["a"]["archived"]


def test_interrupted_media_delivery_is_retried_without_touching_the_note(settings):
    keep = FakeKeep(make_note("a", "Media", attachments={**NO_MEDIA, "images": 1}))

    def interrupted_delivery(message):
        raise OSError("The output could not be delivered")

    with pytest.raises(OSError):
        run(settings, keep, NOW, emit=interrupted_delivery)
    assert json.loads(ledger_lines(settings)[-1])["status"] == "held_pending_report"
    original = (settings.vault / settings.inbox / "Media.md").read_bytes()
    del keep.notes["a"]
    reports = []

    run(settings, keep, NOW, emit=reports.append)

    assert len(reports) == 1 and 'Imported "Media"' in reports[0]
    assert json.loads(ledger_lines(settings)[-1])["status"] == "held"
    assert (settings.vault / settings.inbox / "Media.md").read_bytes() == original
    assert keep.archive_calls == []
    assert run(settings, keep, NOW, emit=reports.append) == []
    assert len(reports) == 1


def test_media_report_acknowledgement_failure_preserves_the_obligation(settings, monkeypatch):
    from mcp_keep.importer import Ledger

    keep = FakeKeep(make_note("a", "Media", attachments={**NO_MEDIA, "audio": 1}))
    delivered = Ledger.report_delivered
    reports = []

    def interrupted_acknowledgement(self, keep_id):
        raise OSError("Interrupted before recording delivery")

    monkeypatch.setattr(Ledger, "report_delivered", interrupted_acknowledgement)
    with pytest.raises(OSError):
        run(settings, keep, NOW, emit=reports.append)
    assert len(reports) == 1
    assert json.loads(ledger_lines(settings)[-1])["status"] == "held_pending_report"
    monkeypatch.setattr(Ledger, "report_delivered", delivered)

    run(settings, keep, NOW, emit=reports.append)

    assert len(reports) == 2 and reports[0] == reports[1]
    assert json.loads(ledger_lines(settings)[-1])["status"] == "held"
    assert keep.archive_calls == []


def test_dry_run_leaves_pending_report_and_ledger_unchanged(settings):
    keep = FakeKeep(make_note("a", "Media", attachments={**NO_MEDIA, "audio": 1}))

    def interrupted_delivery(message):
        raise OSError("The output could not be delivered")

    with pytest.raises(OSError):
        run(settings, keep, NOW, emit=interrupted_delivery)
    before = ledger_lines(settings)
    reports = []

    run(replace(settings, dry_run=True), keep, NOW, emit=reports.append)

    assert len(reports) == 1 and reports[0].startswith("Would report:")
    assert ledger_lines(settings) == before
    assert keep.archive_calls == []


def test_end_to_end_through_the_real_worker(keep_fixture, settings):
    path, ids, _ = keep_fixture
    worker = Path(__file__).with_name("fixture_worker.py")
    report = run(settings, KeepGateway([sys.executable, str(worker)]), NOW)
    assert report == []
    assert inbox_files(settings) == ["Groceries.md"]
    content = (settings.vault / "Inbox" / "Groceries.md").read_text(encoding="utf-8")
    assert content.endswith("---\n- [ ] Fruit\n\t- [x] Apples\n- [ ] Coffee\n")
    assert execute_request("read_note", {"note_id": ids["checklist"]}, FixtureKeep)["archived"] is True
    assert execute_request("read_note", {"note_id": ids["active"]}, FixtureKeep)["archived"] is False
