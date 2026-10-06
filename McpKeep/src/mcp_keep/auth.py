"""Interactive local setup, separate from the MCP tool surface."""

import argparse
import getpass
import json
import os
import subprocess
import sys
from pathlib import Path

from mcp_keep.config import DEFAULT_TOKEN_FILE, MAX_TOKEN_BYTES, token_path
from mcp_keep.errors import KeepError


def exchange_token(email: str, oauth_token: str) -> str:
    try:
        completed = subprocess.run(
            [sys.executable, "-m", "mcp_keep.auth_exchange"],
            input=json.dumps({"email": email, "oauth_token": oauth_token}),
            capture_output=True,
            encoding="utf-8",
            timeout=120,
            check=False,
            creationflags=subprocess.CREATE_NO_WINDOW if sys.platform == "win32" else 0,
        )
        response = json.loads(completed.stdout)
        token = response.get("token")
        if completed.returncode != 0 or response.get("ok") is not True or not isinstance(token, str):
            raise ValueError("No usable token")
        if not token or len(token) > MAX_TOKEN_BYTES or any(not 33 <= ord(character) <= 126 for character in token):
            raise ValueError("No usable token")
    except (OSError, ValueError, AttributeError, subprocess.TimeoutExpired):
        raise KeepError(
            "Google token exchange failed. Repeat the EmbeddedSetup sign-in and try a fresh oauth_token."
        ) from None
    return token


def save_token(path: Path, token: str) -> None:
    path.parent.mkdir(parents=True, exist_ok=True, mode=0o700)
    try:
        descriptor = os.open(path, os.O_WRONLY | os.O_CREAT | os.O_EXCL, 0o600)
    except FileExistsError:
        raise KeepError("A token file already exists. Use a new path, then update KEEP_MASTER_TOKEN_FILE.") from None
    try:
        with os.fdopen(descriptor, "wb") as stream:
            if sys.platform == "win32":
                subprocess.run(
                    ["icacls", str(path), "/inheritance:r", "/grant:r", f"{getpass.getuser()}:F"],
                    capture_output=True,
                    timeout=10,
                    check=True,
                    creationflags=subprocess.CREATE_NO_WINDOW,
                )
            stream.write((token + "\r\n").encode("ascii"))
    except Exception:
        # Remove only the new file this invocation created, never an existing token.
        path.unlink(missing_ok=True)
        raise KeepError("Could not save a protected token file. Check folder permissions and try a new path.") from None


def main() -> None:
    parser = argparse.ArgumentParser(description="Set up a Google master token without exposing it to MCP clients.")
    parser.add_argument("--email", required=True, help="Google account email")
    parser.add_argument(
        "--token-file", default=str(DEFAULT_TOKEN_FILE), help="Absolute token path outside Git checkouts"
    )
    arguments = parser.parse_args()
    try:
        email = arguments.email.strip()
        if not email or "@" not in email:
            raise KeepError("Provide the Google account email with --email.")
        path = token_path(arguments.token_file)
        if path.exists():
            raise KeepError("A token file already exists. Use a new path, then update KEEP_MASTER_TOKEN_FILE.")
        print("Sign in at https://accounts.google.com/EmbeddedSetup and obtain the oauth_token cookie.")
        cookie = getpass.getpass("Paste oauth_token (hidden): ").strip()
        if not cookie or len(cookie) > MAX_TOKEN_BYTES:
            raise KeepError("Provide a nonempty oauth_token cookie.")
        token = exchange_token(email, cookie)
        save_token(path, token)
        print(f"Saved the protected token file to {path}. Configure KEEP_EMAIL and KEEP_MASTER_TOKEN_FILE in Hermes.")
    except KeepError as error:
        print(str(error), file=sys.stderr)
        sys.exit(1)
    except (OSError, EOFError, KeyboardInterrupt):
        print("Authentication setup did not complete. Check permissions and run the command again.", file=sys.stderr)
        sys.exit(1)


if __name__ == "__main__":
    main()
