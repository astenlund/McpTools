using System.ComponentModel;
using System.Globalization;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;

namespace McpWeb.Tools;

internal class Context
{
    private readonly ILogger<Context> _logger;

    public Context(ILogger<Context> logger)
    {
        _logger = logger;
    }

    [McpServerTool]
    [Description("Get current date, time, and timezone information. Use this to understand what year, month, and date it is right now.")]
    public Task<string> GetContext()
    {
        try
        {
            var now = DateTime.Now;
            var timezone = TimeZoneInfo.Local;
            var culture = CultureInfo.CurrentCulture;

            // Get week number
            var calendar = culture.Calendar;
            var weekRule = culture.DateTimeFormat.CalendarWeekRule;
            var firstDayOfWeek = culture.DateTimeFormat.FirstDayOfWeek;
            var weekNumber = calendar.GetWeekOfYear(now, weekRule, firstDayOfWeek);

            // Format the response as a concise, natural sentence
            var utcOffset = timezone.BaseUtcOffset >= TimeSpan.Zero ? $"+{timezone.BaseUtcOffset.Hours}" : timezone.BaseUtcOffset.Hours.ToString();
            var result = $"Current date and time: {now.ToString("dddd, MMMM d, yyyy 'at' HH:mm:ss", culture)}. Timezone: {timezone.Id} (UTC{utcOffset}). This is week {weekNumber} of {now.Year}.";

            _logger.LogInformation("Context retrieved successfully");

            return Task.FromResult(result);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error retrieving context");
            return Task.FromResult($"Error retrieving context: {ex.Message}");
        }
    }
}
