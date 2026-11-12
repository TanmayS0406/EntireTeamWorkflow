using Etw.Azure.Services;
using Etw.Hosting;
using Etw.Shared;
using Etw.Shared.Models;

var builder = WebApplication.CreateBuilder(args);
builder.AddEtwService(EtwConstants.AzureService, EtwConstants.AzurePort);

builder.Services.AddHttpClient("arm");
builder.Services.AddHttpClient("kudu", c => c.Timeout = TimeSpan.FromMinutes(10));
builder.Services.AddSingleton<ArmClient>();
builder.Services.AddSingleton<AppServiceDeployer>();

var app = builder.Build();
app.UseEtwApiKey();

app.MapGet("/health", () => Results.Ok(new { status = "ok", service = EtwConstants.AzureService }));

app.MapGet("/status", (ArmClient arm) => arm.Status());
app.MapPut("/config", (OAuthConfigDto config, ArmClient arm) => { arm.SaveConfig(config); return Results.NoContent(); });
app.MapPost("/connect", (OAuthConnectRequest request, ArmClient arm) => { arm.Connect(request); return Results.Ok(arm.Status()); });
app.MapPost("/disconnect", (ArmClient arm) => { arm.Disconnect(); return Results.NoContent(); });

app.MapGet("/subscriptions", (ArmClient arm, CancellationToken ct) => Guard(() => arm.SubscriptionsAsync(ct)));
app.MapGet("/subscriptions/{id}/resourcegroups", (string id, ArmClient arm, CancellationToken ct) => Guard(() => arm.ResourceGroupsAsync(id, ct)));
app.MapGet("/subscriptions/{id}/locations", (string id, ArmClient arm, CancellationToken ct) => Guard(() => arm.LocationsAsync(id, ct)));
app.MapPost("/subscriptions/{id}/resourcegroups", (string id, AzureResourceGroupDto group, ArmClient arm, CancellationToken ct) =>
    Guard(() => arm.EnsureResourceGroupAsync(id, group.Name, group.Location, ct)));

// Long-running: the orchestrator waits for the answer. Progress is returned as the log of the finished deployment.
app.MapPost("/deploy", async (DeployRequest request, AppServiceDeployer deployer, CancellationToken ct) => Results.Ok(await deployer.DeployAsync(request, ct)));

app.Run();

static async Task<IResult> Guard<T>(Func<Task<T>> action)
{
    try
    {
        return Results.Ok(await action());
    }
    catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or FormatException or HttpRequestException)
    {
        return Results.Json(new { error = ex.Message }, statusCode: ex is ArgumentException or FormatException ? 400 : 502);
    }
}
