import sys
from pathlib import Path

import pytest
from mcp import Client, StdioServerParameters
from mcp.client.stdio import stdio_client
from support import load_state, write_state

pytestmark = pytest.mark.anyio


def parameters(keep_fixture, *, real_server=False):
    path, _, token = keep_fixture
    args = ["-m", "mcp_keep.server"] if real_server else [str(Path(__file__).with_name("fixture_server.py"))]
    return StdioServerParameters(
        command=sys.executable,
        args=args,
        env={
            "KEEP_EMAIL": "account@example.com",
            "KEEP_MASTER_TOKEN_FILE": str(token),
            "KEEP_TEST_STATE_FILE": str(path),
            "KEEP_TIMEOUT_SECONDS": "10",
        },
    )


async def test_real_stdio_discovery_works_without_credentials(keep_fixture, tmp_path):
    params = parameters(keep_fixture, real_server=True)
    params.env["KEEP_EMAIL"] = ""
    params.env["KEEP_TIMEOUT_SECONDS"] = "invalid"
    log = tmp_path / "stderr.log"
    with log.open("w", encoding="utf-8", newline="") as stderr:
        async with Client(stdio_client(params, errlog=stderr), mode="legacy", read_timeout_seconds=20) as client:
            tools = (await client.list_tools()).tools
            assert {tool.name for tool in tools} == {"list_notes", "read_note", "archive_note", "unarchive_note"}
            by_name = {tool.name: tool for tool in tools}
            assert by_name["read_note"].annotations.read_only_hint is True
            assert by_name["archive_note"].annotations.idempotent_hint is True
            result = await client.call_tool("list_notes", {})
            assert result.is_error is True
            assert "KEEP_TIMEOUT_SECONDS" in result.content[0].text
    assert "secret-master-token" not in log.read_text(encoding="utf-8")


async def test_real_stdio_missing_email_is_a_tool_error(keep_fixture):
    params = parameters(keep_fixture, real_server=True)
    params.env["KEEP_EMAIL"] = ""
    async with Client(params, mode="legacy", read_timeout_seconds=20) as client:
        result = await client.call_tool("list_notes", {})
        assert result.is_error is True
        assert "KEEP_EMAIL" in result.content[0].text


async def test_stdio_note_and_checklist_archive_round_trip(keep_fixture):
    _, ids, _ = keep_fixture
    async with Client(parameters(keep_fixture), mode="legacy", read_timeout_seconds=20) as client:
        listing = await client.call_tool("list_notes", {"state": "all", "limit": 1})
        assert listing.is_error is False
        assert len(listing.structured_content["notes"]) == 1
        before = await client.call_tool("read_note", {"note_id": ids["checklist"]})
        assert before.structured_content["items"][1]["parent_item_id"] == ids["parent"]
        archived = await client.call_tool("archive_note", {"note_id": ids["checklist"]})
        assert archived.structured_content == {"id": ids["checklist"], "archived": True, "changed": True}
        noop = await client.call_tool("archive_note", {"note_id": ids["checklist"]})
        assert noop.structured_content["changed"] is False
        restored = await client.call_tool("unarchive_note", {"note_id": ids["checklist"]})
        assert restored.structured_content["archived"] is False
        after = await client.call_tool("read_note", {"note_id": ids["checklist"]})
        assert after.structured_content["items"] == before.structured_content["items"]
        assert after.structured_content["text"] == before.structured_content["text"]


async def test_stdio_preserves_unicode_content(keep_fixture):
    path, ids, _ = keep_fixture
    state = load_state(path)
    text = "M" + chr(0x00E5) + "ndag"
    for node in state["keep"]["nodes"]:
        if node.get("parentId") == ids["active"] and node.get("type") == "LIST_ITEM":
            node["text"] = text
    write_state(path, state)
    async with Client(parameters(keep_fixture), mode="legacy") as client:
        result = await client.call_tool("read_note", {"note_id": ids["active"]})
        assert result.structured_content["text"] == text


async def test_stdio_failure_recovery_and_secret_suppression(keep_fixture, tmp_path):
    path, ids, _ = keep_fixture
    log = tmp_path / "stderr.log"
    with log.open("w", encoding="utf-8", newline="") as stderr:
        async with Client(stdio_client(parameters(keep_fixture), errlog=stderr), mode="legacy") as client:
            for failure in ("auth", "read", "write"):
                state = load_state(path)
                state["failure"] = failure
                write_state(path, state)
                operation = "archive_note" if failure == "write" else "read_note"
                result = await client.call_tool(operation, {"note_id": ids["active"]})
                assert result.is_error is True
                assert "secret-master-token" not in result.content[0].text
                if failure == "write":
                    assert "may have reached Google" in result.content[0].text
                state = load_state(path)
                state["failure"] = None
                write_state(path, state)
                recovered = await client.call_tool("read_note", {"note_id": ids["active"]})
                assert recovered.is_error is False
                assert recovered.structured_content["archived"] is False
    assert "secret-master-token" not in log.read_text(encoding="utf-8")
    assert load_state(path)["syncs"] == 0


@pytest.mark.parametrize("arguments", [{"limit": 3.5}, {"limit": True}, {"offset": -1}, {"state": "trash"}])
async def test_stdio_invalid_arguments_fail_without_backend_access(keep_fixture, arguments):
    path, _, _ = keep_fixture
    before = path.read_bytes()
    async with Client(parameters(keep_fixture), mode="legacy") as client:
        result = await client.call_tool("list_notes", arguments)
        assert result.is_error is True
    assert path.read_bytes() == before
