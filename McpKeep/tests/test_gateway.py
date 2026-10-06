import json
import subprocess
import sys
import threading
import time
from concurrent.futures import ThreadPoolExecutor
from types import SimpleNamespace

import pytest

from mcp_keep.errors import KeepError
from mcp_keep.gateway import KeepGateway


def test_gateway_serializes_overlapping_calls(monkeypatch):
    active = 0
    maximum = 0
    counter_lock = threading.Lock()

    def worker(*args, **kwargs):
        nonlocal active, maximum
        with counter_lock:
            active += 1
            maximum = max(maximum, active)
        time.sleep(0.03)
        with counter_lock:
            active -= 1
        return SimpleNamespace(returncode=0, stdout=json.dumps({"ok": True, "result": {"notes": []}}))

    monkeypatch.setattr(subprocess, "run", worker)
    gateway = KeepGateway()
    with ThreadPoolExecutor(max_workers=4) as pool:
        results = list(pool.map(lambda _: gateway.call("list_notes"), range(4)))
    assert maximum == 1
    assert results == [{"notes": []}] * 4


def test_timeout_reports_uncertain_write_and_allows_later_call(monkeypatch):
    def timeout(*args, **kwargs):
        raise subprocess.TimeoutExpired("worker", 10, output="secret-master-token", stderr="secret-master-token")

    monkeypatch.setattr(subprocess, "run", timeout)
    gateway = KeepGateway()
    with pytest.raises(KeepError, match="may have reached Google") as failure:
        gateway.call("archive_note", note_id="note-id")
    assert "secret-master-token" not in str(failure.value)
    monkeypatch.setattr(
        subprocess, "run", lambda *args, **kwargs: SimpleNamespace(returncode=0, stdout='{"ok":true,"result":{}}')
    )
    assert gateway.call("read_note", note_id="note-id") == {}


def test_timeout_terminates_worker_before_late_side_effect(tmp_path, monkeypatch):
    worker = tmp_path / "hanging_worker.py"
    marker = tmp_path / "worker-marker"
    worker.write_bytes(
        b"import sys\nimport time\nfrom pathlib import Path\n"
        b"path = Path(sys.argv[1])\npath.write_bytes(b'started')\ntime.sleep(1)\npath.write_bytes(b'late')\n"
    )
    monkeypatch.setattr("mcp_keep.gateway.timeout_seconds", lambda: 0.2)
    gateway = KeepGateway([sys.executable, str(worker), str(marker)])
    with pytest.raises(KeepError, match="may have reached Google"):
        gateway.call("archive_note", note_id="note-id")
    time.sleep(1.1)
    assert marker.read_bytes() == b"started"


@pytest.mark.parametrize("stdout", ["secret-master-token", "[]", '{"ok":true,"result":null}', '{"ok":null}'])
def test_broken_worker_output_never_discloses_raw_data(monkeypatch, stdout):
    monkeypatch.setattr(subprocess, "run", lambda *args, **kwargs: SimpleNamespace(returncode=0, stdout=stdout))
    with pytest.raises(KeepError) as failure:
        KeepGateway().call("list_notes")
    assert "secret-master-token" not in str(failure.value)


def test_busy_gateway_does_not_start_a_worker(monkeypatch):
    monkeypatch.setattr("mcp_keep.gateway.timeout_seconds", lambda: 0.01)
    monkeypatch.setattr(subprocess, "run", lambda *args, **kwargs: pytest.fail("Busy call must not start a worker"))
    gateway = KeepGateway()
    gateway._lock.acquire()
    try:
        with pytest.raises(KeepError, match="busy"):
            gateway.call("archive_note", note_id="note-id")
    finally:
        gateway._lock.release()
