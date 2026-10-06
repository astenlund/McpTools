"""Local configuration without embedding credentials in MCP arguments."""

import os
from pathlib import Path

from mcp_keep.errors import KeepError

MAX_TOKEN_BYTES = 16384
DEFAULT_TOKEN_FILE = Path.home() / ".config" / "mcp-keep" / "master.token"


def token_path(value: str | None = None) -> Path:
    path = DEFAULT_TOKEN_FILE if value is None else Path(value).expanduser()
    if not path.is_absolute():
        raise KeepError("KEEP_MASTER_TOKEN_FILE must be an absolute path.")
    path = path.resolve()
    if any((parent / ".git").exists() for parent in path.parents):
        raise KeepError("The master token file must be outside a Git checkout.")
    return path


def read_credentials() -> tuple[str, str]:
    email = os.environ.get("KEEP_EMAIL")
    if email is None or not email.strip():
        raise KeepError("Configure KEEP_EMAIL and KEEP_MASTER_TOKEN_FILE, then restart the server.")
    path = token_path(os.environ.get("KEEP_MASTER_TOKEN_FILE"))
    try:
        with path.open("rb") as stream:
            raw = stream.read(MAX_TOKEN_BYTES + 1)
        token = raw.decode("utf-8-sig").strip()
    except (OSError, UnicodeError):
        raise KeepError(
            "The master token file is missing, unreadable, or not UTF-8. Run mcp-keep-auth to set it up."
        ) from None
    if len(raw) > MAX_TOKEN_BYTES or not token or any(not 33 <= ord(character) <= 126 for character in token):
        raise KeepError("The master token file must contain one nonempty token.")
    return email.strip(), token


def timeout_seconds() -> int:
    try:
        timeout = int(os.environ.get("KEEP_TIMEOUT_SECONDS", "120"))
    except ValueError:
        raise KeepError("KEEP_TIMEOUT_SECONDS must be an integer between 10 and 300.") from None
    if not 10 <= timeout <= 300:
        raise KeepError("KEEP_TIMEOUT_SECONDS must be an integer between 10 and 300.")
    return timeout
