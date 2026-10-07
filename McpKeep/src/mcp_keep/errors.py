"""Messages safe to return across the worker and MCP boundaries."""


class KeepError(Exception):
    """An anticipated failure with a message that contains no credentials."""


UNCERTAIN_WRITE = (
    "The archive operation could not be confirmed and may have reached Google. "
    "Use read_note to check its current state before deciding whether to retry."
)
NOTE_UNAVAILABLE = "The note is unavailable. Use list_notes to obtain an active or archived note ID."
REQUEST_FAILED = "Google Keep request failed. Check connectivity and credentials, then retry."
AUTH_FAILED = "Google authentication failed. Replace the master token using mcp-keep-auth, then restart the server."
