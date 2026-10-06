"""Stdio MCP surface for Hermes and other local clients."""

import logging
import sys
from functools import partial
from typing import Annotated, Any, Literal

import anyio
from mcp.server import MCPServer
from mcp.server.mcpserver.exceptions import ToolError
from mcp.types import ToolAnnotations
from pydantic import Field

from mcp_keep.errors import KeepError
from mcp_keep.gateway import KeepGateway


def create_server(gateway: KeepGateway | None = None) -> MCPServer:
    gateway = KeepGateway() if gateway is None else gateway
    server = MCPServer("McpKeep", version="0.1.0", log_level="WARNING", subscriptions=False)
    read_annotations = ToolAnnotations(read_only_hint=True, destructive_hint=False, open_world_hint=True)
    archive_annotations = ToolAnnotations(
        read_only_hint=False, destructive_hint=False, idempotent_hint=True, open_world_hint=True
    )

    async def call(operation: str, **arguments: Any) -> dict[str, Any]:
        try:
            return await anyio.to_thread.run_sync(partial(gateway.call, operation, **arguments))
        except KeepError as error:
            raise ToolError(str(error)) from None

    @server.tool(annotations=read_annotations)
    async def list_notes(
        state: Literal["active", "archived", "all"] = "active",
        query: Annotated[str, Field(max_length=1000)] = "",
        limit: Annotated[int, Field(ge=1, le=200, strict=True)] = 50,
        offset: Annotated[int, Field(ge=0, strict=True)] = 0,
    ) -> dict[str, Any]:
        """List notes by archive state and optional title/body substring. Follow next_offset for more pages."""
        return await call("list_notes", state=state, query=query, limit=limit, offset=offset)

    @server.tool(annotations=read_annotations)
    async def read_note(note_id: Annotated[str, Field(min_length=1, max_length=256)]) -> dict[str, Any]:
        """Read text, labels, archive status, and checklist items for a note ID returned by list_notes."""
        return await call("read_note", note_id=note_id)

    @server.tool(annotations=archive_annotations)
    async def archive_note(note_id: Annotated[str, Field(min_length=1, max_length=256)]) -> dict[str, Any]:
        """Archive one note without changing its contents. Already archived is a successful no-op."""
        return await call("archive_note", note_id=note_id)

    @server.tool(annotations=archive_annotations)
    async def unarchive_note(note_id: Annotated[str, Field(min_length=1, max_length=256)]) -> dict[str, Any]:
        """Unarchive one note without changing its contents. Already active is a successful no-op."""
        return await call("unarchive_note", note_id=note_id)

    return server


def main() -> None:
    logging.basicConfig(level=logging.WARNING, stream=sys.stderr, format="%(levelname)s: %(message)s")
    create_server().run(transport="stdio")


if __name__ == "__main__":
    main()
