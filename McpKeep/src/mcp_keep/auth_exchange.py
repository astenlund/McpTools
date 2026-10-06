"""Private, bounded-process token exchange used by the local setup command."""

import json
import logging
import sys
from uuid import getnode

import gpsoauth

from mcp_keep.config import MAX_TOKEN_BYTES


def main() -> None:
    logging.disable(logging.CRITICAL)
    try:
        request = json.loads(sys.stdin.read(65537))
        response = gpsoauth.exchange_token(request["email"], request["oauth_token"], f"{getnode():x}")
        token = response.get("Token")
        if not isinstance(token, str) or not token or len(token) > MAX_TOKEN_BYTES:
            raise ValueError("No usable token")
        if any(not 33 <= ord(character) <= 126 for character in token):
            raise ValueError("No usable token")
        result = {"ok": True, "token": token}
    except Exception:
        # Never print Google response bodies or the pasted cookie.
        result = {"ok": False}
    sys.stdout.write(json.dumps(result, ensure_ascii=True) + "\n")


if __name__ == "__main__":
    main()
