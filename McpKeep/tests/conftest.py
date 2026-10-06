import pytest
from support import seed_state


@pytest.fixture
def anyio_backend():
    return "asyncio"


@pytest.fixture
def keep_fixture(tmp_path, monkeypatch):
    token = tmp_path / "master.token"
    token.write_bytes(b"secret-master-token\r\n")
    state_path = tmp_path / "state.json"
    ids = seed_state(state_path)
    monkeypatch.setenv("KEEP_EMAIL", "account@example.com")
    monkeypatch.setenv("KEEP_MASTER_TOKEN_FILE", str(token))
    monkeypatch.setenv("KEEP_TEST_STATE_FILE", str(state_path))
    monkeypatch.setenv("KEEP_TIMEOUT_SECONDS", "10")
    return state_path, ids, token
