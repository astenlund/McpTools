import sys
from pathlib import Path

from mcp_keep.gateway import KeepGateway
from mcp_keep.server import create_server

if __name__ == "__main__":
    worker = Path(__file__).with_name("fixture_worker.py")
    create_server(KeepGateway([sys.executable, str(worker)])).run(transport="stdio")
