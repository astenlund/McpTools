import json
import os
import subprocess
from types import SimpleNamespace

import pytest

from mcp_keep.auth import exchange_token, save_token
from mcp_keep.config import read_credentials, timeout_seconds, token_path
from mcp_keep.errors import KeepError


def test_token_bom_and_newline_are_removed(keep_fixture):
    _, _, token = keep_fixture
    token.write_bytes(bytes([239, 187, 191]) + b"secret-master-token\r\n")
    assert read_credentials() == ("account@example.com", "secret-master-token")


@pytest.mark.parametrize("raw", [b"", b"two tokens", b"invalid\xff", b"x" * 16385])
def test_unusable_tokens_fail_safely(keep_fixture, raw):
    _, _, token = keep_fixture
    token.write_bytes(raw)
    with pytest.raises(KeepError):
        read_credentials()


def test_missing_credentials_are_actionable(keep_fixture, monkeypatch):
    monkeypatch.delenv("KEEP_EMAIL")
    with pytest.raises(KeepError, match="KEEP_EMAIL"):
        read_credentials()
    monkeypatch.setenv("KEEP_EMAIL", "account@example.com")
    keep_fixture[2].unlink()
    with pytest.raises(KeepError, match="missing, unreadable"):
        read_credentials()


def test_token_file_must_be_absolute_and_outside_git(tmp_path):
    with pytest.raises(KeepError, match="absolute"):
        token_path("relative.token")
    (tmp_path / ".git").mkdir()
    with pytest.raises(KeepError, match="outside a Git checkout"):
        token_path(str(tmp_path / "secret.token"))


@pytest.mark.parametrize("value", ["0", "301", "3.5", "invalid", ""])
def test_invalid_timeout_is_rejected(value, monkeypatch):
    monkeypatch.setenv("KEEP_TIMEOUT_SECONDS", value)
    with pytest.raises(KeepError, match="between 10 and 300"):
        timeout_seconds()


def test_token_save_never_overwrites_existing_token(tmp_path):
    path = tmp_path / "new.token"
    save_token(path, "master-token")
    assert path.read_bytes() == b"master-token\r\n"
    with pytest.raises(KeepError, match="already exists"):
        save_token(path, "replacement")
    assert path.read_bytes() == b"master-token\r\n"
    if os.name != "nt":
        assert path.stat().st_mode & 0o777 == 0o600


@pytest.mark.parametrize("response", [{"ok": False, "secret": "pasted-cookie"}, {"ok": True, "token": None}, []])
def test_token_exchange_errors_do_not_disclose_cookie(response, monkeypatch):
    monkeypatch.setattr(
        subprocess, "run", lambda *args, **kwargs: SimpleNamespace(returncode=0, stdout=json.dumps(response))
    )
    with pytest.raises(KeepError, match="token exchange failed") as failure:
        exchange_token("account@example.com", "pasted-cookie")
    assert "pasted-cookie" not in str(failure.value)


def test_token_exchange_success(monkeypatch):
    monkeypatch.setattr(
        subprocess,
        "run",
        lambda *args, **kwargs: SimpleNamespace(returncode=0, stdout='{"ok":true,"token":"master-token"}'),
    )
    assert exchange_token("account@example.com", "pasted-cookie") == "master-token"
