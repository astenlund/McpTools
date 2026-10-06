"""Disposable backend process. Its stdout carries only the private JSON response."""

import json
import logging
import math
import os
import sys
import threading
import time

from mcp_keep.backend import execute_request
from mcp_keep.errors import REQUEST_FAILED, KeepError


def main(client_factory=None) -> None:
    logging.disable(logging.CRITICAL)
    watchdog = None
    try:
        request = json.loads(sys.stdin.read(65537))
        if not isinstance(request, dict) or not isinstance(request.get("arguments"), dict):
            raise KeepError("Invalid Google Keep request.")
        deadline = request.get("deadline")
        if type(deadline) not in (int, float) or not math.isfinite(deadline):
            raise KeepError("Invalid Google Keep request deadline.")
        remaining = deadline - time.monotonic()
        if not 0 < remaining <= 300:
            raise KeepError("The Google Keep request expired before it could run.")
        # Bound the network lifetime even if the parent server is terminated.
        watchdog = threading.Timer(remaining, os._exit, args=(124,))
        watchdog.daemon = True
        watchdog.start()
        if client_factory is None:
            result = execute_request(request.get("operation"), request["arguments"])
        else:
            result = execute_request(request.get("operation"), request["arguments"], client_factory)
        response = {"ok": True, "result": result}
    except KeepError as error:
        response = {"ok": False, "error": str(error)}
    except Exception:
        # Google responses and exception messages can contain credentials.
        response = {"ok": False, "error": REQUEST_FAILED}
    finally:
        if watchdog is not None:
            watchdog.cancel()
    sys.stdout.write(json.dumps(response, ensure_ascii=True) + "\n")


if __name__ == "__main__":
    main()
