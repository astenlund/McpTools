using System.Text;

using McpConsultant.Models;

namespace McpConsultant.Services;

internal sealed record FileValidationResult(IReadOnlyList<AttachedFile> Files, string? Error);

/// <summary>
/// Two-stage attachment validation per the spec's File attachment rules:
/// stage 1 metadata (dedup, qualification, existence, budget from FileInfo.Length),
/// stage 2 content (read, NUL scan, strict UTF-8), each stage reporting every failing path at once.
/// </summary>
internal static class FileAttachmentValidator
{
    // Every message is built from the ErrorMessages.StableSubstrings table so the file
    // classes have exactly one source of truth (ErrorMessages owns the substrings).
    internal static FileValidationResult Validate(IReadOnlyList<string?>? files, long maxAttachmentBytes)
    {
        if (files is null || files.Count == 0)
        {
            return new FileValidationResult([], null);
        }

        var stage1Errors = new List<string>();
        var reportedInvalid = new HashSet<string>(StringComparer.Ordinal);
        var candidates = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var element in files)
        {
            // Guard runs before the predicate: Path.IsPathFullyQualified throws on null.
            if (string.IsNullOrWhiteSpace(element) || !Path.IsPathFullyQualified(element))
            {
                var display = element ?? "(null)";
                if (reportedInvalid.Add(display))
                {
                    stage1Errors.Add($"'{display}' {ErrorMessages.StableSubstrings["file-not-fully-qualified"]}");
                }

                continue;
            }

            if (!seen.Add(element))
            {
                continue; // silent dedup, first occurrence wins
            }

            candidates.Add(element);
        }

        long totalBytes = 0;
        var passing = new List<(string Path, long Length)>();
        foreach (var path in candidates)
        {
            if (Directory.Exists(path))
            {
                stage1Errors.Add($"'{path}' {ErrorMessages.StableSubstrings["file-directory"]}");

                continue;
            }

            if (!File.Exists(path))
            {
                stage1Errors.Add($"'{path}' {ErrorMessages.StableSubstrings["file-missing"]}");

                continue;
            }

            try
            {
                var length = new FileInfo(path).Length;
                totalBytes += length;
                passing.Add((path, length));
            }
            catch (Exception ex)
            {
                // Metadata fault after the existence check (vanished file, unreachable share)
                stage1Errors.Add($"'{path}' {ErrorMessages.StableSubstrings["file-unreadable"]}: {ex.Message}");
            }
        }

        if (totalBytes > maxAttachmentBytes)
        {
            // Sizes come from the guarded pass above; re-reading metadata here could throw unguarded.
            var sizes = string.Join(", ", passing.Select(p => $"'{p.Path}' ({p.Length} bytes)"));
            stage1Errors.Add($"attached files {ErrorMessages.StableSubstrings["file-over-budget"]} of {maxAttachmentBytes} bytes: {sizes}");
        }

        if (stage1Errors.Count > 0)
        {
            return new FileValidationResult([], $"Error: file validation failed: {string.Join("; ", stage1Errors)}.");
        }

        var stage2Errors = new List<string>();
        var attached = new List<AttachedFile>();
        var strictUtf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

        foreach (var (path, _) in passing)
        {
            byte[] bytes;
            try
            {
                bytes = File.ReadAllBytes(path);
            }
            catch (Exception ex)
            {
                stage2Errors.Add($"'{path}' {ErrorMessages.StableSubstrings["file-unreadable"]}: {ex.Message}");

                continue;
            }

            if (bytes.Contains((byte)0))
            {
                stage2Errors.Add($"'{path}' {ErrorMessages.StableSubstrings["file-binary"]}");

                continue;
            }

            try
            {
                attached.Add(new AttachedFile(path, strictUtf8.GetString(bytes)));
            }
            catch (DecoderFallbackException)
            {
                stage2Errors.Add($"'{path}' {ErrorMessages.StableSubstrings["file-wrong-encoding"]}");
            }
        }

        if (stage2Errors.Count > 0)
        {
            return new FileValidationResult([], $"Error: file validation failed: {string.Join("; ", stage2Errors)}.");
        }

        return new FileValidationResult(attached, null);
    }
}
