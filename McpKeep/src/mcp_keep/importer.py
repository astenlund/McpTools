"""Copy Google Keep notes into an Obsidian vault inbox, then archive them in Keep.

A run handles at most ``limit`` notes, oldest first. Each import writes the note
file, reads it back, appends the Keep ID to a ledger inside the vault, and only
then archives the note. Every crash point leaves state the next run resumes from:

- file written, ledger line missing: the inbox file carrying the same keep_id is
  found and reused, so no duplicate appears;
- ledger line written, archive missing or uncertain: the current note and its
  copy are checked before retrying, ahead of new imports;
- archive confirmed, completion line missing: the archived state is observed
  and recorded without another write to Keep;
- ledger line cut short mid-write: the incomplete line never took effect and is
  dropped.

Notes with media the text cannot carry are imported but left in Keep, recorded as
held, and reported once. Pinned notes and notes with the skip label stay in Keep.
"""

import argparse
import hashlib
import json
import os
import re
import sys
from collections.abc import Callable, Iterator
from contextlib import contextmanager
from dataclasses import dataclass
from datetime import datetime
from pathlib import Path
from typing import Any, Protocol

from yaml import BaseLoader, MappingNode, ScalarNode, YAMLError, compose

from mcp_keep.backend import snapshot_fingerprint
from mcp_keep.errors import NOTE_UNAVAILABLE, KeepError

LEDGER_DIR = ".keep-import"
LEDGER_FILE = "ledger.jsonl"
DEFAULT_LOCK_FILE = Path.home() / ".config" / "mcp-keep" / "import.lock"
PAGE_SIZE = 200
MAX_NAME_LENGTH = 80
FORBIDDEN_NAME_CHARACTERS = re.compile(r'[\x00-\x1f<>:"/\\|?*#^\[\]]')
RESERVED_NAMES = {"CON", "PRN", "AUX", "NUL", *(f"COM{n}" for n in range(1, 10)), *(f"LPT{n}" for n in range(1, 10))}
TAG_FORBIDDEN_CHARACTERS = re.compile(r"[^\w/-]")
ATTACHMENT_NOUNS = {
    "images": ("image", "images"),
    "drawings": ("drawing", "drawings"),
    "audio": ("audio clip", "audio clips"),
    "other": ("other attachment", "other attachments"),
}
IMPORTED = "imported"
HELD = "held"
COMPLETED = "completed"
HELD_PENDING_REPORT = "held_pending_report"
STUCK = "Until then, every run stops here and imports nothing new."


class ImportFailure(Exception):
    """A run that stopped before finishing. The message is safe to deliver."""


class ImportBusy(Exception):
    """Another import run holds the lock."""


class Gateway(Protocol):
    def call(self, operation: str, **arguments: Any) -> dict[str, Any]: ...


@dataclass(frozen=True)
class Settings:
    vault: Path
    inbox: str = "Inbox"
    skip_label: str = "keep"
    limit: int = 1
    dry_run: bool = False
    lock_file: Path = DEFAULT_LOCK_FILE


class Ledger:
    """Append-only record of imported Keep IDs. The last entry for an ID wins."""

    def __init__(self, path: Path) -> None:
        self.path = path
        self._entries: dict[str, dict[str, str]] = {}

    def load(self, repair: bool) -> None:
        if not self.path.exists():
            return
        data = self.path.read_bytes()
        complete, newline, partial = data.rpartition(b"\n")
        if partial and repair:
            with self.path.open("r+b") as stream:
                stream.truncate(len(complete) + len(newline))
        for number, line in enumerate(complete.split(b"\n") if newline else [], start=1):
            try:
                entry = json.loads(line)
            except ValueError:
                entry = None
            if (
                not isinstance(entry, dict)
                or not isinstance(entry.get("keep_id"), str)
                or entry.get("status") not in (IMPORTED, HELD, COMPLETED, HELD_PENDING_REPORT)
                or not isinstance(entry.get("path"), str)
                or (entry.get("status") == HELD_PENDING_REPORT and not isinstance(entry.get("report"), str))
            ):
                raise ImportFailure(f"Line {number} of {self.path} is unreadable. Fix or remove it, then rerun.")
            self._entries[entry["keep_id"]] = entry

    def status(self, keep_id: str) -> str | None:
        entry = self._entries.get(keep_id)
        return None if entry is None else entry["status"]

    def path_of(self, keep_id: str) -> str:
        return self._entries[keep_id]["path"]

    def pending_ids(self) -> list[str]:
        return [keep_id for keep_id, entry in self._entries.items() if entry["status"] == IMPORTED]

    def complete(self, keep_id: str) -> None:
        self.append({**self._entries[keep_id], "status": COMPLETED})

    def pending_reports(self) -> list[dict[str, str]]:
        return [entry for entry in self._entries.values() if entry["status"] == HELD_PENDING_REPORT]

    def report_delivered(self, keep_id: str) -> None:
        entry = {key: value for key, value in self._entries[keep_id].items() if key != "report"}
        self.append({**entry, "status": HELD})

    def append(self, entry: dict[str, str]) -> None:
        self.path.parent.mkdir(parents=True, exist_ok=True)
        with self.path.open("ab") as stream:
            stream.write(json.dumps(entry, ensure_ascii=True).encode("ascii") + b"\n")
            stream.flush()
            os.fsync(stream.fileno())
        self._entries[entry["keep_id"]] = entry


def run(settings: Settings, gateway: Gateway, now: datetime, emit: Callable[[str], None] | None = None) -> list[str]:
    """Emit reports as notes are handled and return the same lines; empty means nothing to report."""
    vault = settings.vault
    if not vault.is_dir():
        raise ImportFailure(f"The vault folder {vault} does not exist.")
    inbox = vault / settings.inbox
    if not inbox.resolve().is_relative_to(vault.resolve()):
        raise ImportFailure(f"The inbox {settings.inbox} must be a folder inside the vault.")
    with _exclusive(settings.lock_file):
        ledger_path = vault / LEDGER_DIR / LEDGER_FILE
        _inside_vault(ledger_path, vault)
        ledger = Ledger(ledger_path)
        ledger.load(repair=not settings.dry_run)
        report: list[str] = []
        reported: set[str] = set()
        report_sink = _print_report if emit is None else emit

        def deliver(messages: list[str]) -> None:
            for message in messages:
                report.append(message)
                report_sink(message)

        def deliver_held() -> None:
            for entry in ledger.pending_reports():
                keep_id = entry["keep_id"]
                if keep_id in reported:
                    continue
                message = entry["report"]
                deliver([f"Would report: {message}" if settings.dry_run else message])
                reported.add(keep_id)
                if not settings.dry_run:
                    ledger.report_delivered(keep_id)

        deliver_held()
        active = _active_notes(gateway)
        active_by_id = {note["id"]: note for note in active}
        pending = [
            keep_id
            for keep_id in ledger.pending_ids()
            if keep_id not in active_by_id or _eligible(active_by_id[keep_id], settings.skip_label)
        ][: settings.limit]
        for keep_id in pending:
            deliver(_finish_archive(settings, gateway, ledger, keep_id))
        remaining = settings.limit - len(pending)
        if remaining:
            fresh = sorted(
                (note for note in active if ledger.status(note["id"]) is None and _eligible(note, settings.skip_label)),
                key=_age,
            )
            for note in fresh[:remaining]:
                deliver(_import(settings, gateway, ledger, note["id"], now))
                deliver_held()
        return report


def _active_notes(gateway: Gateway) -> list[dict[str, Any]]:
    notes: dict[str, dict[str, Any]] = {}
    offset = 0
    while offset is not None:
        page = _keep(gateway, "list_notes", state="active", limit=PAGE_SIZE, offset=offset)
        notes.update((note["id"], note) for note in page["notes"])
        offset = page["next_offset"]
    return list(notes.values())


def _eligible(note: dict[str, Any], skip_label: str) -> bool:
    labels = {label.casefold() for label in note["labels"]}
    return not note["pinned"] and not note["archived"] and skip_label.casefold() not in labels


def _age(note: dict[str, Any]) -> tuple[bool, str, str]:
    created = note.get("created")
    return created is None, created or "", note["id"]


def _import(settings: Settings, gateway: Gateway, ledger: Ledger, keep_id: str, now: datetime) -> list[str]:
    note = _keep(gateway, "read_note", note_id=keep_id)
    if not _eligible(note, settings.skip_label):
        return []
    missing = describe_attachments(note["attachments"])
    content = render(note, missing, now)
    inbox = settings.vault / settings.inbox
    recovered = _find_by_keep_id(inbox, keep_id)
    target = recovered or _unique_path(inbox, note_name(note))
    _inside_vault(target, settings.vault)
    relative = target.relative_to(settings.vault).as_posix()
    if recovered:
        now = _verify_copy(target, note, relative)
        content = render(note, missing, now)
    if settings.dry_run:
        outcome = f"leave it in Keep ({missing} not copied)" if missing else "archive it in Keep"
        return [f"Would write {relative} and {outcome}:\n\n{content}"]
    inbox.mkdir(parents=True, exist_ok=True)
    if recovered is None:
        _write_verified(target, content, settings.vault / LEDGER_DIR, keep_id, relative)
    status = HELD_PENDING_REPORT if missing else IMPORTED
    entry = {"keep_id": keep_id, "status": status, "path": relative, "at": now.isoformat(timespec="seconds")}
    if missing:
        title = " ".join(note["title"].split()) or target.stem
        entry["report"] = (
            f'Imported "{title}" to {relative} but left it in Keep: it has {missing} that could not be copied. '
            "Save them yourself, then archive the note in Keep."
        )
    ledger.append(entry)
    if missing:
        return []
    return _finish_archive(settings, gateway, ledger, keep_id)


def _finish_archive(settings: Settings, gateway: Gateway, ledger: Ledger, keep_id: str) -> list[str]:
    relative = ledger.path_of(keep_id)
    try:
        note = gateway.call("read_note", note_id=keep_id)
    except KeepError as error:
        if str(error) != NOTE_UNAVAILABLE:
            raise ImportFailure(f"Reading Google Keep failed: {error}") from None
        raise ImportFailure(
            f"The Keep note for {relative} is in Keep's trash or deleted, so its import cannot finish. "
            "Restore it in Keep and the next run archives it. If it is gone for good, "
            f'mark it done as described under "Resolving a stuck note" in README_KEEP.md. {STUCK}'
        ) from None
    if note["archived"]:
        if settings.dry_run:
            return [f"Would record the confirmed archive of {relative}."]
        ledger.complete(keep_id)
        return []
    if not _eligible(note, settings.skip_label):
        return []
    target = settings.vault / relative
    _inside_vault(target, settings.vault)
    _verify_copy(target, note, relative)
    if describe_attachments(note["attachments"]):
        raise ImportFailure(
            f"{relative} has new attachments in Keep, so the note was left there. "
            f"Save them next to the copy, then archive the note in Keep yourself. {STUCK}"
        )
    if settings.dry_run:
        return [f"Would archive the already imported note behind {relative} in Keep."]
    try:
        gateway.call("archive_note", note_id=keep_id, expected_fingerprint=snapshot_fingerprint(note))
    except KeepError as error:
        raise ImportFailure(
            f"{relative} was imported, but archiving it failed: {error} The next run rechecks the note and copy."
        ) from None
    ledger.complete(keep_id)
    return []


def _inside_vault(target: Path, vault: Path) -> None:
    if not target.resolve().is_relative_to(vault.resolve()):
        raise ImportFailure("An import copy points outside the vault. Fix its path before rerunning.")


def _verify_copy(target: Path, note: dict[str, Any], relative: str) -> datetime:
    if not target.is_file():
        raise ImportFailure(
            f"The copy {relative} is missing, so the note was left in Keep. Restore the copy, "
            f"or archive the note in Keep yourself if you no longer want it. {STUCK}"
        )
    provenance = _provenance(target)
    try:
        imported_at = datetime.fromisoformat(provenance.get("imported", ""))
    except ValueError:
        raise ImportFailure(
            f"The imported time in the frontmatter of {relative} is missing or unreadable, so the note was left in "
            f"Keep. Restore that line, or archive the note in Keep yourself. {STUCK}"
        ) from None
    expected = render(note, describe_attachments(note["attachments"]), imported_at).encode("utf-8")
    if provenance.get("keep_id") != note["id"] or target.read_bytes() != expected:
        raise ImportFailure(
            f"{relative} no longer matches its Keep note, so the note was left in Keep. "
            f"Reconcile the two, then archive the note in Keep yourself. {STUCK}"
        )
    return imported_at


def _keep(gateway: Gateway, operation: str, **arguments: Any) -> dict[str, Any]:
    try:
        return gateway.call(operation, **arguments)
    except KeepError as error:
        raise ImportFailure(f"Reading Google Keep failed: {error}") from None


def describe_attachments(counts: dict[str, int]) -> str | None:
    parts = [
        f"{count} {ATTACHMENT_NOUNS[kind][count != 1]}"
        for kind, count in counts.items()
        if count and kind in ATTACHMENT_NOUNS
    ]
    if not parts:
        return None
    return parts[0] if len(parts) == 1 else ", ".join(parts[:-1]) + " and " + parts[-1]


def render(note: dict[str, Any], missing: str | None, imported_at: datetime) -> str:
    """The note as Markdown with Keep provenance in its frontmatter, LF line endings."""
    lines = ["---", f"keep_id: {_quote(note['id'])}"]
    if note["title"].strip():
        lines.append(f"keep_title: {_quote(note['title'])}")
    for key, field in (("keep_created", "created"), ("keep_edited", "edited")):
        if note.get(field):
            lines.append(f"{key}: {_quote(note[field])}")
    tags = label_tags(note["labels"])
    if tags:
        lines.append("tags:")
        lines.extend(f"  - {_quote(tag)}" for tag in tags)
    if missing:
        lines.append(f"keep_not_copied: {_quote(missing)}")
    lines += [f"imported: {_quote(imported_at.isoformat(timespec='seconds'))}", "---"]
    if missing:
        lines += [
            "> [!warning] Not fully imported",
            f"> This note has {missing} in Google Keep that could not be copied here. It was left in Keep.",
            "",
        ]
    lines += _body(note)
    text = _normalize(note["text"])
    links = [link for link in note["links"] if link["url"] and link["url"] not in text]
    if links:
        lines += ["", "## Links", ""]
        lines.extend(f"- {_link(link)}" for link in links)
    result = "\n".join(lines)
    return result if result.endswith("\n") else result + "\n"


def _body(note: dict[str, Any]) -> list[str]:
    if note["type"] != "checklist":
        return _normalize(note["text"]).split("\n")
    depth: dict[str, int] = {}
    lines = []
    for item in note["items"]:
        parent = item["parent_item_id"]
        level = depth.get(parent, -1) + 1 if parent else 0
        depth[item["id"]] = level
        first, *rest = _normalize(item["text"]).split("\n")
        lines.append("\t" * level + f"- [{'x' if item['checked'] else ' '}] {first}")
        lines.extend("\t" * level + "  " + line for line in rest)
    return lines


def _normalize(text: str) -> str:
    return text.replace("\r\n", "\n").replace("\r", "\n")


def _quote(value: str) -> str:
    # A JSON string is a valid YAML double-quoted scalar.
    return json.dumps(value, ensure_ascii=False)


def _link(link: dict[str, Any]) -> str:
    title = (link.get("title") or link["url"]).replace("[", "\\[").replace("]", "\\]")
    url = link["url"]
    return f"[{title}](<{url}>)" if re.search(r"[\s()<>]", url) else f"[{title}]({url})"


def label_tags(labels: list[str]) -> list[str]:
    """Obsidian tags allow letters, digits, _, - and /, and cannot be purely numeric."""
    tags: dict[str, str] = {}
    for label in labels:
        tag = TAG_FORBIDDEN_CHARACTERS.sub("", re.sub(r"\s+", "-", label.strip()))
        if tag and not tag.isdigit():
            tags.setdefault(tag.casefold(), tag)
    return list(tags.values())


def note_name(note: dict[str, Any]) -> str:
    """A file name from the title, else the first line of content, else Untitled."""
    # A checklist's text is generated from its items with checkbox glyphs, so name it from the items.
    content = [item["text"] for item in note["items"]] if note["type"] == "checklist" else note["text"].split("\n")
    for candidate in [note["title"], *content]:
        name = " ".join(FORBIDDEN_NAME_CHARACTERS.sub(" ", candidate).split())
        if len(name) > MAX_NAME_LENGTH:
            cut = name[:MAX_NAME_LENGTH]
            name = cut.rsplit(" ", 1)[0] if " " in cut else cut
        name = name.strip(" .")
        if name:
            return name + "_" if name.split(".")[0].upper() in RESERVED_NAMES else name
    return "Untitled"


def _unique_path(folder: Path, name: str) -> Path:
    candidate = folder / f"{name}.md"
    number = 2
    while candidate.exists():
        candidate = folder / f"{name} ({number}).md"
        number += 1
    return candidate


def _find_by_keep_id(folder: Path, keep_id: str) -> Path | None:
    if not folder.is_dir():
        return None
    matches = []
    for path in sorted(folder.glob("*.md")):
        if _provenance(path).get("keep_id") == keep_id:
            matches.append(path)
    if len(matches) > 1:
        raise ImportFailure(f"More than one inbox file has keep_id {_quote(keep_id)}. Reconcile them before rerunning.")
    return matches[0] if matches else None


def _provenance(path: Path) -> dict[str, str]:
    fields: dict[str, list[str]] = {}
    multiline: set[str] = set()
    current_key: str | None = None
    closed = False
    with path.open("rb") as stream:
        if stream.readline(5).rstrip(b"\r\n") != b"---":
            return {}
        for raw in stream:
            line = raw.decode("utf-8", errors="replace").rstrip("\r\n")
            if line == "---":
                closed = True
                break
            stripped = line.lstrip(" \t")
            if not stripped or stripped.startswith("#"):
                continue
            if line.startswith((" ", "\t")):
                if current_key is not None:
                    multiline.add(current_key)
                continue
            key, separator, value = line.partition(":")
            key = key.rstrip(" \t")
            current_key = (
                key if separator and key in ("keep_id", "imported") and (not value or value[0] in " \t") else None
            )
            if current_key is not None:
                values = fields.setdefault(key, [])
                if len(values) < 2:
                    values.append(value)
    if "keep_id" not in fields:
        return {}
    if not closed:
        raise ImportFailure(f"The frontmatter of {path} is never closed. Fix it before rerunning.")
    if multiline:
        raise ImportFailure(
            f"The frontmatter of {path} has multiline provenance. Keep its identity and import time on single lines."
        )
    result: dict[str, str] = {}
    for key, values in fields.items():
        if len(values) != 1:
            raise ImportFailure(f"The frontmatter of {path} has a duplicate {key}. Reconcile it before rerunning.")
        parsed = _yaml_scalar(values[0])
        if parsed is None:
            raise ImportFailure(f"The frontmatter of {path} has an unreadable {key}. Fix it before rerunning.")
        result[key] = parsed
    return result


def _yaml_scalar(text: str) -> str | None:
    """Decode one-line string scalars, preserving date-shaped text and rejecting unsupported YAML."""
    if "\n" in text or "\r" in text:
        return None
    try:
        document = compose("value: " + text.strip(" \t"), Loader=BaseLoader)
    except (YAMLError, RecursionError):
        return None
    if not isinstance(document, MappingNode) or len(document.value) != 1:
        return None
    node = document.value[0][1]
    if not isinstance(node, ScalarNode) or node.style not in (None, "'", '"') or node.tag != "tag:yaml.org,2002:str":
        return None
    if node.style is None and not node.value:
        return None
    return node.value


def _write_verified(target: Path, content: str, scratch: Path, keep_id: str, relative: str) -> None:
    data = content.encode("utf-8")
    scratch.mkdir(parents=True, exist_ok=True)
    temporary = scratch / (hashlib.sha256(keep_id.encode("utf-8")).hexdigest()[:16] + ".tmp")
    temporary.unlink(missing_ok=True)
    with temporary.open("xb") as stream:
        stream.write(data)
        stream.flush()
        os.fsync(stream.fileno())
    os.link(temporary, target)
    temporary.unlink()
    if target.read_bytes() != data:
        raise ImportFailure(f"{relative} did not read back as written, so the note was not archived.")


@contextmanager
def _exclusive(path: Path) -> Iterator[None]:
    """An OS file lock, released by the OS even when the process dies."""
    path.parent.mkdir(parents=True, exist_ok=True)
    with path.open("a+b") as handle:
        try:
            _lock(handle, locked=True)
        except OSError:
            raise ImportBusy from None
        try:
            yield
        finally:
            _lock(handle, locked=False)


def _lock(handle, locked: bool) -> None:
    if sys.platform == "win32":
        import msvcrt

        handle.seek(0)
        msvcrt.locking(handle.fileno(), msvcrt.LK_NBLCK if locked else msvcrt.LK_UNLCK, 1)
    else:
        import fcntl

        fcntl.flock(handle.fileno(), (fcntl.LOCK_EX | fcntl.LOCK_NB) if locked else fcntl.LOCK_UN)


def _print_report(message: str) -> None:
    print(message, flush=True)


def main(argv: list[str] | None = None, gateway: Gateway | None = None) -> int:
    parser = argparse.ArgumentParser(prog="keep-import", description="Import Google Keep notes into an Obsidian inbox.")
    parser.add_argument("--vault", type=Path, required=True, help="Absolute path of the Obsidian vault.")
    parser.add_argument("--inbox", default="Inbox", help="Inbox folder, relative to the vault.")
    parser.add_argument("--skip-label", default="keep", help="Notes with this label stay in Keep.")
    parser.add_argument("--limit", type=int, default=1, help="Most notes to handle in one run.")
    parser.add_argument("--lock-file", type=Path, default=DEFAULT_LOCK_FILE, help="Lock that serializes runs.")
    parser.add_argument("--dry-run", action="store_true", help="Print what would happen without changing anything.")
    args = parser.parse_args(argv)
    if args.limit < 1:
        parser.error("--limit must be at least 1")
    if hasattr(sys.stdout, "reconfigure"):
        sys.stdout.reconfigure(encoding="utf-8", errors="replace")
    settings = Settings(args.vault, args.inbox, args.skip_label, args.limit, args.dry_run, args.lock_file)
    if gateway is None:
        from mcp_keep.gateway import KeepGateway

        gateway = KeepGateway()
    try:
        run(settings, gateway, datetime.now().astimezone(), emit=_print_report)
    except ImportBusy:
        print("Another Keep import is running; this run was skipped.", file=sys.stderr)
        return 0
    except (ImportFailure, OSError) as failure:
        print(f"Keep import failed: {failure}")
        return 1
    return 0


if __name__ == "__main__":
    sys.exit(main())
