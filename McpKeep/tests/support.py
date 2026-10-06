"""Offline Google Keep fixture using the real gkeepapi note representation."""

import json
import os
from pathlib import Path

import gkeepapi


def write_state(path: Path, state: dict) -> None:
    path.write_bytes((json.dumps(state) + "\r\n").encode("utf-8"))


def load_state(path: Path) -> dict:
    return json.loads(path.read_bytes())


def clean_client(client: gkeepapi.Keep) -> None:
    for node in client._findDirtyNodes():
        node.save()
    for label in client.labels():
        label.save()


def seed_state(path: Path) -> dict[str, str]:
    client = gkeepapi.Keep()
    active = client.createNote("Inbox", "Buy Milk\nPlan a trip")
    active.pinned = True
    label = client.createLabel("Personal")
    active.labels.add(label)
    archived = client.createNote("Old plans", "Keep this")
    archived.archived = True
    checklist = client.createList("Groceries", [])
    parent = checklist.add("Fruit", False, 30000)
    child = checklist.add("Apples", True, 20000)
    parent.indent(child)
    checklist.add("Coffee", False, 10000)
    trashed = client.createNote("In trash", "Hidden")
    trashed.trash()
    deleted = client.createNote("Deleted", "Hidden")
    deleted.delete()
    clean_client(client)
    write_state(path, {"keep": client.dump(), "failure": None, "syncs": 0})
    return {
        "active": active.id,
        "archived": archived.id,
        "checklist": checklist.id,
        "trashed": trashed.id,
        "deleted": deleted.id,
        "child": child.id,
        "parent": parent.id,
    }


class FixtureKeep(gkeepapi.Keep):
    def authenticate(self, email, master_token, **kwargs):
        self.state_path = Path(os.environ["KEEP_TEST_STATE_FILE"])
        state = load_state(self.state_path)
        if state["failure"] == "auth":
            raise gkeepapi.exception.LoginException("secret-master-token")
        if state["failure"] == "read":
            raise RuntimeError("secret-master-token")
        self.restore(state["keep"])
        self._keep_api.observe({"nodes": [note.save() for note in self.all()]})

    def sync(self, **kwargs):
        state = load_state(self.state_path)
        if state["failure"] == "write":
            raise RuntimeError("secret-master-token")
        clean_client(self)
        state["keep"] = self.dump()
        state["syncs"] += 1
        write_state(self.state_path, state)
        self._keep_api.observe({"nodes": [note.save() for note in self.all()]})
