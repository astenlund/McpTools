using McpConsultant.Models;
using McpConsultant.Services;

namespace McpConsultant.Tests;

public class PromptAssemblerTests
{
    [Fact]
    public void QuestionOnly_EmitsQuestionDelimiterAndQuestionLast()
    {
        // Arrange / Act
        var message = PromptAssembler.BuildUserMessage("What now?", null, []);

        // Assert
        Assert.Equal("===== QUESTION =====\nWhat now?", message.Trim());
        Assert.DoesNotContain("CONTEXT", message);
        Assert.DoesNotContain("FILE", message);
    }

    [Fact]
    public void ContextPresent_IsBracketedAndPrecedesQuestion()
    {
        // Arrange / Act
        var message = PromptAssembler.BuildUserMessage("Q", "some background", []);

        // Assert
        var beginContext = message.IndexOf("===== BEGIN CONTEXT =====", StringComparison.Ordinal);
        var endContext = message.IndexOf("===== END CONTEXT =====", StringComparison.Ordinal);
        var question = message.IndexOf("===== QUESTION =====", StringComparison.Ordinal);
        Assert.True(beginContext >= 0 && endContext > beginContext && question > endContext);
        Assert.Contains("some background", message);
    }

    [Fact]
    public void WhitespaceContext_TreatedAsAbsent()
    {
        // Arrange / Act
        var message = PromptAssembler.BuildUserMessage("Q", "   ", []);

        // Assert
        Assert.DoesNotContain("CONTEXT", message);
    }

    [Fact]
    public void Files_AreBracketedInOrder_BeforeQuestion()
    {
        // Arrange
        var files = new List<AttachedFile>
        {
            new(@"C:\a\one.txt", "alpha"),
            new(@"C:\b\two.txt", "beta"),
        };

        // Act
        var message = PromptAssembler.BuildUserMessage("Q", null, files);

        // Assert
        var one = message.IndexOf(@"===== BEGIN FILE: C:\a\one.txt =====", StringComparison.Ordinal);
        var oneEnd = message.IndexOf(@"===== END FILE: C:\a\one.txt =====", StringComparison.Ordinal);
        var two = message.IndexOf(@"===== BEGIN FILE: C:\b\two.txt =====", StringComparison.Ordinal);
        var question = message.IndexOf("===== QUESTION =====", StringComparison.Ordinal);
        Assert.True(one >= 0 && oneEnd > one && two > oneEnd && question > two);
    }
}
