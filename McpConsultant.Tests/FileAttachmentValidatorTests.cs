using McpConsultant.Services;

namespace McpConsultant.Tests;

public class FileAttachmentValidatorTests
{
    [Fact]
    public void NullOrEmptyList_SucceedsWithNoFiles()
    {
        // Arrange / Act
        var r1 = FileAttachmentValidator.Validate(null, 100);
        var r2 = FileAttachmentValidator.Validate([], 100);

        // Assert
        Assert.Null(r1.Error);
        Assert.Empty(r1.Files);
        Assert.Null(r2.Error);
        Assert.Empty(r2.Files);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("relative/path.txt")]
    [InlineData(@"\rooted-but-not-qualified.txt")]
    public void NotFullyQualified_FailsThatBranch(string? path)
    {
        // Arrange / Act
        var result = FileAttachmentValidator.Validate([path], 100);

        // Assert
        Assert.NotNull(result.Error);
        Assert.Contains("is not a fully qualified path", result.Error);
    }

    [Fact]
    public void DistinctInvalidValues_BothReported_DuplicateInvalidValueOnce()
    {
        // Arrange / Act - two distinct invalid values plus a repeat of the first
        var result = FileAttachmentValidator.Validate(["relative/a.txt", "", "relative/a.txt"], 100);

        // Assert
        Assert.NotNull(result.Error);
        Assert.Contains("'relative/a.txt' is not a fully qualified path", result.Error);
        Assert.Contains("'' is not a fully qualified path", result.Error);
        Assert.Equal(2, result.Error.Split("is not a fully qualified path").Length - 1);
    }

    [Fact]
    public void MissingAndDirectory_BothReportedInOneError()
    {
        // Arrange
        var dir = Directory.CreateTempSubdirectory().FullName;
        try
        {
            var missing = Path.Combine(dir, "nope.txt");

            // Act
            var result = FileAttachmentValidator.Validate([missing, dir], 100);

            // Assert
            Assert.NotNull(result.Error);
            Assert.Contains("does not exist", result.Error);
            Assert.Contains("is a directory, not a file", result.Error);
            Assert.Contains(missing, result.Error);
            Assert.Contains(dir, result.Error);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void OverBudget_RejectedWithoutContentRead()
    {
        // Arrange - hold an exclusive lock so any content read would fail as unreadable;
        // the budget error appearing instead proves no content read occurred.
        var dir = Directory.CreateTempSubdirectory().FullName;
        try
        {
            var path = Path.Combine(dir, "big.txt");
            File.WriteAllText(path, new string('x', 200));
            using var exclusiveLock = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None);

            // Act
            var result = FileAttachmentValidator.Validate([path], maxAttachmentBytes: 100);

            // Assert
            Assert.NotNull(result.Error);
            Assert.Contains("exceed the attachment budget", result.Error);
            Assert.DoesNotContain("could not be read", result.Error);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void DuplicatePath_ValidatedCountedAndAttachedOnce()
    {
        // Arrange
        var dir = Directory.CreateTempSubdirectory().FullName;
        try
        {
            var path = Path.Combine(dir, "dup.txt");
            File.WriteAllText(path, new string('y', 60));
            var upper = path.ToUpperInvariant();

            // Act - 60 bytes twice would exceed 100; deduplicated it fits, and exactly at the
            // limit (60) it also fits, covering the at-limit branch of the budget arithmetic
            var result = FileAttachmentValidator.Validate([path, upper], maxAttachmentBytes: 100);
            var atLimit = FileAttachmentValidator.Validate([path], maxAttachmentBytes: 60);

            // Assert
            Assert.Null(result.Error);
            Assert.Single(result.Files);
            Assert.Null(atLimit.Error);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void LockedFile_UnderBudget_ReportedAsUnreadableInStageTwo()
    {
        // Arrange - metadata (stage 1) succeeds under the lock; the content read (stage 2) fails
        var dir = Directory.CreateTempSubdirectory().FullName;
        try
        {
            var path = Path.Combine(dir, "locked.txt");
            File.WriteAllText(path, "content");
            using var exclusiveLock = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None);

            // Act
            var result = FileAttachmentValidator.Validate([path], maxAttachmentBytes: 100);

            // Assert
            Assert.NotNull(result.Error);
            Assert.Contains("could not be read", result.Error);
            Assert.DoesNotContain("exceed the attachment budget", result.Error);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void NulByte_ReportedAsBinary()
    {
        // Arrange
        var dir = Directory.CreateTempSubdirectory().FullName;
        try
        {
            var path = Path.Combine(dir, "bin.dat");
            File.WriteAllBytes(path, [0x61, 0x00, 0x62]);

            // Act
            var result = FileAttachmentValidator.Validate([path], 100);

            // Assert
            Assert.NotNull(result.Error);
            Assert.Contains("appears to be binary (contains a NUL byte)", result.Error);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void InvalidUtf8WithoutNul_ReportedAsWrongEncoding()
    {
        // Arrange - 0xE4 alone is not valid UTF-8 and contains no NUL
        var dir = Directory.CreateTempSubdirectory().FullName;
        try
        {
            var path = Path.Combine(dir, "latin1.txt");
            File.WriteAllBytes(path, [0x61, 0xE4, 0x62]);

            // Act
            var result = FileAttachmentValidator.Validate([path], 100);

            // Assert
            Assert.NotNull(result.Error);
            Assert.Contains("is not valid UTF-8", result.Error);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void ValidFiles_ReturnedInOrderWithContent()
    {
        // Arrange
        var dir = Directory.CreateTempSubdirectory().FullName;
        try
        {
            var a = Path.Combine(dir, "a.txt");
            var b = Path.Combine(dir, "b.txt");
            File.WriteAllText(a, "alpha");
            File.WriteAllText(b, "beta");

            // Act
            var result = FileAttachmentValidator.Validate([b, a], 100);

            // Assert
            Assert.Null(result.Error);
            Assert.Equal(2, result.Files.Count);
            Assert.Equal(b, result.Files[0].Path);
            Assert.Equal("beta", result.Files[0].Content);
            Assert.Equal(a, result.Files[1].Path);
            Assert.Equal("alpha", result.Files[1].Content);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
