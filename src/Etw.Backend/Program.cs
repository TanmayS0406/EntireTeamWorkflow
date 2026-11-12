using Etw.Backend.Data;
using Etw.Backend.Endpoints;
using Etw.Backend.Hubs;
using Etw.Backend.Services;
using Etw.Hosting;
using Etw.Shared;
using Microsoft.EntityFrameworkCore;
using Yarp.ReverseProxy.Configuration;

var builder = WebApplication.CreateBuilder(args);
builder.AddEtwService(EtwConstants.BackendService, EtwConstants.BackendPort);

var dbPath = Environment.GetEnvironmentVariable("ETW_DB") is { Length: > 0 } custom ? custom : Path.Combine(EtwPaths.Data, "etw.db");
builder.Services.AddDbContextFactory<EtwDb>(options => options.UseSqlite($"Data Source={dbPath}"));

builder.Services.AddSignalR().AddJsonProtocol(options =>
{
    options.PayloadSerializerOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter());
    options.PayloadSerializerOptions.DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull;
});

builder.Services.AddEtwClient("orchestrator", EtwConstants.OrchestratorUrl);
builder.Services.AddEtwClient("integrations", EtwConstants.IntegrationsUrl);
builder.Services.AddSingleton<SettingsService>();
builder.Services.AddSingleton<OrchestratorClient>();
builder.Services.AddSingleton<IntegrationsClient>();
builder.Services.AddSingleton<RunService>();

// The Azure DevOps / Slack / Teams and Azure services are reached through this gateway, so the desktop app only needs one address.
builder.Services.AddReverseProxy().LoadFromMemory(
    routes:
    [
        ProxyRoute("integrations", "/api/integrations"),
        ProxyRoute("azure", "/api/azure"),
    ],
    clusters:
    [
        ProxyCluster("integrations", EtwConstants.IntegrationsUrl),
        ProxyCluster("azure", EtwConstants.AzureUrl),
    ]);

var app = builder.Build();

await using (var scope = app.Services.CreateAsyncScope())
{
    var db = await scope.ServiceProvider.GetRequiredService<IDbContextFactory<EtwDb>>().CreateDbContextAsync();
    await db.Database.EnsureCreatedAsync();
    await SchemaUpgrade.RunAsync(db);
}

app.UseEtwApiKey();
app.MapSystemEndpoints();
app.MapProjectEndpoints();
app.MapRunEndpoints();
app.MapHub<RunsHub>("/hubs/runs");
app.MapReverseProxy();

app.Run();

static RouteConfig ProxyRoute(string cluster, string prefix) => new()
{
    RouteId = cluster,
    ClusterId = cluster,
    Match = new RouteMatch { Path = prefix + "/{**catch-all}" },
    Transforms = [new Dictionary<string, string> { ["PathRemovePrefix"] = prefix }],
};

static ClusterConfig ProxyCluster(string id, string address) => new()
{
    ClusterId = id,
    Destinations = new Dictionary<string, DestinationConfig> { ["d1"] = new() { Address = address } },
    HttpRequest = new Yarp.ReverseProxy.Forwarder.ForwarderRequestConfig { ActivityTimeout = TimeSpan.FromMinutes(3) },
};
