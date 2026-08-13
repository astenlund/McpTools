namespace McpConsultant.Models;

/// <summary>A validated attachment: the fully qualified path and its UTF-8 text content.</summary>
internal sealed record AttachedFile(string Path, string Content);
