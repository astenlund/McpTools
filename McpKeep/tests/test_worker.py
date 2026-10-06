import json
import subprocess
import sys
import time

import pytest


@pytest.mark.parametrize("deadline", [None, True, "later", 0])
def test_bad_worker_deadline_fails_without_network(deadline, keep_fixture):
    completed = subprocess.run(
        [sys.executable, "-m", "mcp_keep.worker"],
        input=json.dumps({"operation": "list_notes", "arguments": {}, "deadline": deadline}),
        encoding="utf-8",
        capture_output=True,
        check=True,
        timeout=10,
    )
    response = json.loads(completed.stdout)
    assert response["ok"] is False
    assert "deadline" in response["error"] or "expired" in response["error"]


def test_worker_owns_deadline_even_when_parent_does_not_enforce_it(tmp_path):
    fixture = tmp_path / "blocked_worker.py"
    fixture.write_bytes(
        b"import time\nimport gkeepapi\nfrom mcp_keep.worker import main\n"
        b"class HangingKeep(gkeepapi.Keep):\n    def authenticate(self, *args):\n        time.sleep(10)\n"
        b"main(HangingKeep)\n"
    )
    token = tmp_path / "token"
    token.write_bytes(b"synthetic-token")
    import os

    environment = dict(os.environ, KEEP_EMAIL="account@example.com", KEEP_MASTER_TOKEN_FILE=str(token))
    started = time.monotonic()
    completed = subprocess.run(
        [sys.executable, str(fixture)],
        input=json.dumps({"operation": "list_notes", "arguments": {}, "deadline": started + 3}),
        env=environment,
        encoding="utf-8",
        capture_output=True,
        check=False,
        timeout=6,
    )
    assert completed.returncode == 124
    assert time.monotonic() - started < 5
    assert "synthetic-token" not in completed.stderr
