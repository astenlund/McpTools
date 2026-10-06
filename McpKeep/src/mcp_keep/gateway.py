"""Serialize calls and bound a fresh worker's entire network lifetime."""

import json
import subprocess
import sys
import threading
import time
from typing import Any

from mcp_keep.backend import validate_request
from mcp_keep.config import timeout_seconds
from mcp_keep.errors import REQUEST_FAILED, UNCERTAIN_WRITE, KeepError


class KeepGateway:
    def __init__(self, worker_command: list[str] | None = None) -> None:
        self._worker_command = [sys.executable, "-m", "mcp_keep.worker"] if worker_command is None else worker_command
        self._lock = threading.Lock()

    def call(self, operation: str, **arguments: Any) -> dict[str, Any]:
        validate_request(operation, arguments)
        timeout = timeout_seconds()
        deadline = time.monotonic() + timeout
        if not self._lock.acquire(timeout=timeout):
            raise KeepError("Google Keep is busy. Retry after the current call finishes.")
        try:
            remaining = deadline - time.monotonic()
            if remaining <= 0:
                raise KeepError("Google Keep is busy. Retry after the current call finishes.")
            try:
                completed = subprocess.run(
                    self._worker_command,
                    input=json.dumps({"operation": operation, "arguments": arguments, "deadline": deadline}),
                    capture_output=True,
                    encoding="utf-8",
                    timeout=remaining,
                    check=False,
                    creationflags=subprocess.CREATE_NO_WINDOW if sys.platform == "win32" else 0,
                )
            except subprocess.TimeoutExpired:
                message = (
                    UNCERTAIN_WRITE
                    if operation in ("archive_note", "unarchive_note")
                    else "Google Keep timed out. Retry later."
                )
                raise KeepError(message) from None
            except OSError:
                raise KeepError("Unable to start the Google Keep worker. Check the Python installation.") from None
            except UnicodeError:
                message = UNCERTAIN_WRITE if operation in ("archive_note", "unarchive_note") else REQUEST_FAILED
                raise KeepError(message) from None
            try:
                response = json.loads(completed.stdout)
                if completed.returncode != 0 or not isinstance(response, dict) or type(response.get("ok")) is not bool:
                    raise ValueError("Invalid worker response")
                if response["ok"]:
                    if not isinstance(response.get("result"), dict):
                        raise ValueError("Invalid worker result")
                    return response["result"]
                if not isinstance(response.get("error"), str):
                    raise ValueError("Invalid worker error")
            except (ValueError, UnicodeError):
                message = UNCERTAIN_WRITE if operation in ("archive_note", "unarchive_note") else REQUEST_FAILED
                raise KeepError(message) from None
            raise KeepError(response["error"])
        finally:
            self._lock.release()
