using McpWeb.Models;
using McpWeb.Services;
using McpWeb.Tools;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

var builder = Host.CreateApplicationBuilder(args);

// Add configuration from appsettings.json, environment variables, and command line
builder.Configuration
    .SetBasePath(Directory.GetCurrentDirectory())
    .AddJsonFile("appsettings.json", optional: true, reloadOnChange: true)
    .AddJsonFile($"appsettings.{builder.Environment.EnvironmentName}.json", optional: true, reloadOnChange: true)
    .AddEnvironmentVariables()
    .AddCommandLine(args);

// Configure all logs to go to stderr (stdout is used for the MCP protocol messages).
builder.Logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);

// Register configuration sections as options
builder.Services.Configure<SearchSettings>(
    builder.Configuration.GetSection("Search"));
builder.Services.Configure<FetchSettings>(
    builder.Configuration.GetSection("Fetch"));

// Register HttpClient instances for our services
builder.Services.AddHttpClient<SerperSearcher>()
    .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
    {
        AutomaticDecompression = System.Net.DecompressionMethods.All
    });
builder.Services.AddHttpClient<ContentFetcher>()
    .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
    {
        AutomaticDecompression = System.Net.DecompressionMethods.All
    });

// Register our services
builder.Services.AddSingleton<VpnDetectionService>();
builder.Services.AddSingleton<SearchService>();

// Add the MCP services: the transport to use (stdio) and the tools to register.
builder.Services
    .AddMcpServer()
    .WithStdioServerTransport()
    .WithTools<Search>()
    .WithTools<Fetch>()
    .WithTools<Context>();

await builder.Build().RunAsync();
