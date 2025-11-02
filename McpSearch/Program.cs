using McpSearch.Services;
using McpSearch.Tools;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

var builder = Host.CreateApplicationBuilder(args);

// Configure all logs to go to stderr (stdout is used for the MCP protocol messages).
builder.Logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);

// Register HttpClient instances for our services
builder.Services.AddHttpClient<DuckDuckGoSearcher>();
builder.Services.AddHttpClient<ContentFetcher>();

// Register our search services
builder.Services.AddSingleton<SearchService>();

// Add the MCP services: the transport to use (stdio) and the tools to register.
builder.Services
    .AddMcpServer()
    .WithStdioServerTransport()
    .WithTools<SearchTools>();

await builder.Build().RunAsync();
