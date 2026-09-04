using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Aftermath.Cli;
using Aftermath.Configuration;

// Copies the exemplar exactly (InternalToolExemplar/src/Acme.ClaudeDb/Program.cs:11-29). MCP hosts
// launch this with no extra args ("dotnet run --project ..." passes nothing through). Any
// args present mean a human invoked it directly from a terminal — run the one-shot CLI
// instead of starting the stdio MCP server, then exit with its result code.
if (args.Length > 0)
{
    return await CliRunner.RunAsync(args);
}

HostApplicationBuilder builder = Host.CreateApplicationBuilder(args);

// MCP uses stdout for protocol traffic — all logging must go to stderr.
builder.Logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);

builder.Services.AddSingleton(WorkspaceRegistry.Build(Environment.GetEnvironmentVariable));

builder.Services
    .AddMcpServer()
    .WithStdioServerTransport()
    .WithToolsFromAssembly();

await builder.Build().RunAsync();
return 0;
