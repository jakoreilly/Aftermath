using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Aftermath.Cli;
using Aftermath.Configuration;
using Serilog;
using Serilog.Events;
using Serilog.Sinks.Grafana.Loki;

// Copies the exemplar exactly (InternalToolExemplar/src/Acme.ClaudeDb/Program.cs:11-29). MCP hosts
// launch this with no extra args ("dotnet run --project ..." passes nothing through). Any
// args present mean a human invoked it directly from a terminal — run the one-shot CLI
// instead of starting the stdio MCP server, then exit with its result code.
if (args.Length > 0)
{
    return await CliRunner.RunAsync(args);
}

// MCP uses stdout for protocol traffic — every log line MUST go to stderr. The console sink
// does that unconditionally; a Grafana Loki sink is added ONLY when INCIDENTTIMELINE_LOKI_URL
// is set, so an unconfigured run behaves exactly as before and still opens no socket
// (hard constraint 1). Label {app="aftermath"} is what a Grafana LogQL selector must match.
LoggerConfiguration logConfig = new LoggerConfiguration()
    .MinimumLevel.Information()
    .Enrich.FromLogContext()
    .WriteTo.Console(standardErrorFromLevel: LogEventLevel.Verbose);

string? lokiUrl = Environment.GetEnvironmentVariable("INCIDENTTIMELINE_LOKI_URL");
if (!string.IsNullOrWhiteSpace(lokiUrl))
{
    logConfig.WriteTo.GrafanaLoki(
        lokiUrl,
        labels: new[] { new LokiLabel { Key = "app", Value = "aftermath" } });
}

Log.Logger = logConfig.CreateLogger();

try
{
    HostApplicationBuilder builder = Host.CreateApplicationBuilder(args);

    builder.Logging.ClearProviders();
    builder.Services.AddSerilog(dispose: true);

    builder.Services.AddSingleton(WorkspaceRegistry.Build(Environment.GetEnvironmentVariable));

    builder.Services
        .AddMcpServer()
        .WithStdioServerTransport()
        .WithToolsFromAssembly();

    await builder.Build().RunAsync();
    return 0;
}
finally
{
    // Short-lived process: flush the Loki batch before exit rather than lose the tail.
    await Log.CloseAndFlushAsync();
}
