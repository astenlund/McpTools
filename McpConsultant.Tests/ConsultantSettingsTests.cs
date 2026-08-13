using McpConsultant.Models;
using Microsoft.Extensions.Configuration;

namespace McpConsultant.Tests;

public class ConsultantSettingsTests
{
    [Fact]
    public void Defaults_WhenSectionAbsent_MatchSpecTable()
    {
        // Arrange
        var config = new ConfigurationBuilder().Build();

        // Act
        var settings = new ConsultantSettings();
        config.GetSection("Consultant").Bind(settings);

        // Assert
        Assert.Null(settings.ApiKey);
        Assert.Equal("openai/gpt-5.2", settings.DefaultModel);
        Assert.Null(settings.SystemPrompt);
        Assert.Equal("high", settings.DefaultEffort);
        Assert.False(settings.RequireVpn);
        Assert.Equal(300, settings.TimeoutSeconds);
        Assert.Equal(1_048_576, settings.MaxAttachmentBytes);
    }

    [Fact]
    public void Overrides_WhenSectionPresent_AreHonored()
    {
        // Arrange
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Consultant:ApiKey"] = "test-key",
                ["Consultant:DefaultModel"] = "x-ai/grok-4",
                ["Consultant:DefaultEffort"] = "low",
                ["Consultant:RequireVpn"] = "true",
                ["Consultant:TimeoutSeconds"] = "60",
                ["Consultant:MaxAttachmentBytes"] = "2048",
            })
            .Build();

        // Act
        var settings = new ConsultantSettings();
        config.GetSection("Consultant").Bind(settings);

        // Assert
        Assert.Equal("test-key", settings.ApiKey);
        Assert.Equal("x-ai/grok-4", settings.DefaultModel);
        Assert.Equal("low", settings.DefaultEffort);
        Assert.True(settings.RequireVpn);
        Assert.Equal(60, settings.TimeoutSeconds);
        Assert.Equal(2048, settings.MaxAttachmentBytes);
    }
}
