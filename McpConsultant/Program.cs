using McpConsultant.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

var builder = Host.CreateApplicationBuilder(args);

// Deliberate deviation from McpWeb: anchor configuration at the executable's directory,
// not the working directory, so a client-spawned server loads its own appsettings.json.
builder.Configuration
    .SetBasePath(AppContext.BaseDirectory)
    .AddJsonFile("appsettings.json", optional: true, reloadOnChange: true)
    .AddJsonFile($"appsettings.{builder.Environment.EnvironmentName}.json", optional: true, reloadOnChange: true)
    .AddEnvironmentVariables()
    .AddCommandLine(args);

// All logs to stderr (stdout carries the MCP protocol). Deliberate deviation from McpWeb:
// timestamps on stderr support the registration-time smoke check. TimestampFormat lives on
// ConsoleFormatterOptions (via AddSimpleConsole); the ConsoleLoggerOptions property of the
// same name is [Obsolete] in .NET 10 and would break the zero-warning build.
builder.Logging.AddSimpleConsole(o => o.TimestampFormat = "[HH:mm:ss.fff] ");
builder.Logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);

builder.Services.Configure<ConsultantSettings>(
    builder.Configuration.GetSection("Consultant"));

builder.Services
    .AddMcpServer()
    .WithStdioServerTransport();

await builder.Build().RunAsync();
