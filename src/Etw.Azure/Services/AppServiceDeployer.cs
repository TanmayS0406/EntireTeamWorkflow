using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Etw.Shared;
using Etw.Shared.Models;

namespace Etw.Azure.Services;

/// <summary>
/// Deploys what the release agent prepared (.etw/deploy/manifest.json) to Azure App Service on Linux: ensures the resource
/// group and plan, creates one web app per component and pushes each folder as a zip through the Kudu zip-deploy API.
/// Everything is done with the signed-in user's own Azure permissions.
/// </summary>
public sealed partial class AppServiceDeployer(ArmClient arm, IHttpClientFactory httpFactory, ILogger<AppServiceDeployer> log)
{
    private const string WebApi = "api-version=2023-12-01";
    private static readonly string[] RuntimePrefixes = ["DOTNETCORE|", "NODE|", "PYTHON|", "JAVA|", "PHP|"];

    private static readonly Dictionary<string, string> SkuTiers = new(StringComparer.OrdinalIgnoreCase)
    {
        ["B1"] = "Basic", ["B2"] = "Basic", ["B3"] = "Basic",
        ["S1"] = "Standard", ["S2"] = "Standard", ["S3"] = "Standard",
        ["P1v3"] = "PremiumV3", ["P2v3"] = "PremiumV3", ["P3v3"] = "PremiumV3",
    };

    public async Task<DeployResultDto> DeployAsync(DeployRequest request, CancellationToken ct)
    {
        var result = new DeployResultDto();
        void Step(string message)
        {
            result.Log.Add(message);
            log.LogInformation("Deploy {Run}: {Message}", request.RunId, message);
        }

        try
        {
            var invalid = Validate(request);
            if (invalid is not null) throw new InvalidOperationException(invalid);

            var manifest = ReadManifest(request.TargetFolder, request.AppNamePrefix);
            var subscription = Guid.Parse(request.SubscriptionId).ToString();

            var group = await arm.EnsureResourceGroupAsync(subscription, request.ResourceGroup, request.Location, ct);
            Step($"Resource group {group.Name} ready in {group.Location}");

            var planName = $"{request.AppNamePrefix}-plan";
            var planId = $"/subscriptions/{subscription}/resourceGroups/{group.Name}/providers/Microsoft.Web/serverfarms/{planName}";
            await arm.SendAsync(HttpMethod.Put, $"https://management.azure.com{planId}?{WebApi}", new JsonObject
            {
                ["location"] = group.Location,
                ["kind"] = "linux",
                ["sku"] = new JsonObject { ["name"] = request.PlanSku.ToUpperInvariant(), ["tier"] = SkuTiers[request.PlanSku] },
                ["properties"] = new JsonObject { ["reserved"] = true },
            }, ct);
            await WaitForAsync($"https://management.azure.com{planId}?{WebApi}", n => n?["properties"]?["provisioningState"]?.GetValue<string>() == "Succeeded", "the App Service plan", ct);
            Step($"App Service plan {planName} ({request.PlanSku}) ready");

            foreach (var component in manifest.Components)
            {
                var site = $"{request.AppNamePrefix}-{component.Name}";
                await CreateSiteAsync(subscription, group, planId, site, component, ct);
                Step($"Web app {site} ready ({component.Runtime})");

                var zip = Path.Combine(EtwPaths.Temp, $"deploy-{Guid.NewGuid():N}.zip");
                try
                {
                    ZipFile.CreateFromDirectory(ResolveInside(request.TargetFolder, component.Directory), zip, CompressionLevel.Optimal, includeBaseDirectory: false);
                    Step($"Packaged {component.Name} ({new FileInfo(zip).Length / 1024 / 1024.0:0.0} MB)");
                    await ZipDeployAsync(site, zip, ct);
                }
                finally
                {
                    try { File.Delete(zip); } catch (IOException) { /* temp file */ }
                }

                result.Urls[component.Name] = $"https://{site}.azurewebsites.net";
                Step($"Deployed {component.Name} → https://{site}.azurewebsites.net");
            }

            result.Succeeded = true;
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or IOException or HttpRequestException or TimeoutException or JsonException or FormatException)
        {
            log.LogWarning("Deployment failed: {Message}", ex.Message);
            result.Error = ex.Message;
        }
        return result;
    }

    // ---- ARM ----------------------------------------------------------------------------------------------------

    private async Task CreateSiteAsync(string subscription, AzureResourceGroupDto group, string planId, string site, DeployComponent component, CancellationToken ct)
    {
        var siteConfig = new JsonObject
        {
            ["linuxFxVersion"] = component.Runtime,
            ["alwaysOn"] = true,
            ["http20Enabled"] = true,
            ["ftpsState"] = "Disabled",
            ["minTlsVersion"] = "1.2",
        };
        if (!string.IsNullOrWhiteSpace(component.StartupCommand)) siteConfig["appCommandLine"] = component.StartupCommand;

        var url = $"https://management.azure.com/subscriptions/{subscription}/resourceGroups/{group.Name}/providers/Microsoft.Web/sites/{site}?{WebApi}";
        try
        {
            await arm.SendAsync(HttpMethod.Put, url, new JsonObject
            {
                ["location"] = group.Location,
                ["kind"] = "app,linux",
                ["properties"] = new JsonObject { ["serverFarmId"] = planId, ["httpsOnly"] = true, ["siteConfig"] = siteConfig },
            }, ct);
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("Conflict", StringComparison.OrdinalIgnoreCase) || ex.Message.Contains("already", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"The web app name '{site}' is already taken by someone else (names are global). Choose another app name prefix. Azure said: {ex.Message}");
        }
        await WaitForAsync(url, n => n?["properties"]?["state"]?.GetValue<string>() == "Running", $"web app {site}", ct);
    }

    /// <summary>Polls a resource until <paramref name="ready"/> is true (provisioning is asynchronous).</summary>
    private async Task WaitForAsync(string url, Func<JsonNode?, bool> ready, string what, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow.AddMinutes(8);
        while (DateTime.UtcNow < deadline)
        {
            if (ready(await arm.SendAsync(HttpMethod.Get, url, null, ct, allowNotFound: true))) return;
            await Task.Delay(TimeSpan.FromSeconds(5), ct);
        }
        throw new TimeoutException($"Timed out waiting for {what} to become ready.");
    }

    // ---- Kudu zip deploy ----------------------------------------------------------------------------------------

    private async Task ZipDeployAsync(string site, string zipPath, CancellationToken ct)
    {
        var http = httpFactory.CreateClient("kudu");
        var token = await arm.TokenAsync(ct);
        var scm = $"https://{site}.scm.azurewebsites.net";

        // A new site's SCM endpoint (and the role assignment behind it) can take a minute to become reachable.
        HttpResponseMessage? accepted = null;
        for (var attempt = 1; attempt <= 8 && accepted is null; attempt++)
        {
            await using var stream = File.OpenRead(zipPath);
            using var content = new StreamContent(stream);
            content.Headers.ContentType = new MediaTypeHeaderValue("application/zip");
            using var request = new HttpRequestMessage(HttpMethod.Post, $"{scm}/api/zipdeploy?isAsync=true") { Content = content };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

            var response = await http.SendAsync(request, ct);
            if (response.StatusCode is HttpStatusCode.Accepted or HttpStatusCode.OK)
            {
                accepted = response;
                break;
            }

            var retryable = response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.NotFound or HttpStatusCode.BadGateway or HttpStatusCode.ServiceUnavailable or HttpStatusCode.Forbidden;
            if (!retryable || attempt == 8) throw new InvalidOperationException($"Zip deploy to {site} was rejected: HTTP {(int)response.StatusCode}.");
            response.Dispose();
            await Task.Delay(TimeSpan.FromSeconds(20), ct);
        }

        var statusUrl = accepted!.Headers.Location?.ToString() ?? $"{scm}/api/deployments/latest";
        accepted.Dispose();

        var deadline = DateTime.UtcNow.AddMinutes(15);
        while (DateTime.UtcNow < deadline)
        {
            await Task.Delay(TimeSpan.FromSeconds(5), ct);
            using var poll = new HttpRequestMessage(HttpMethod.Get, statusUrl);
            poll.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using var response = await http.SendAsync(poll, ct);
            if (response.StatusCode == HttpStatusCode.Accepted || !response.IsSuccessStatusCode) continue;

            var json = JsonNode.Parse(await response.Content.ReadAsStringAsync(ct));
            switch (json?["status"]?.GetValue<int>())
            {
                case 4: return;
                case 3: throw new InvalidOperationException($"Azure could not start {site}: the deployment failed (see https://{site}.scm.azurewebsites.net/api/deployments/latest/log).");
            }
        }
        throw new TimeoutException($"Timed out waiting for the deployment of {site} to finish.");
    }

    // ---- Validation (pure, so it can be tested) -----------------------------------------------------------------

    public static string? Validate(DeployRequest request)
    {
        if (!Guid.TryParse(request.SubscriptionId, out _)) return "Choose an Azure subscription.";
        if (!ResourceGroup().IsMatch(request.ResourceGroup)) return "The resource group name may only contain letters, digits, '-', '_', '.' and '()' (up to 90 characters).";
        if (!Location().IsMatch(request.Location)) return "Choose an Azure region.";
        if (!AppPrefix().IsMatch(request.AppNamePrefix)) return "The app name prefix must be 3–40 lowercase letters, digits or hyphens.";
        if (!SkuTiers.ContainsKey(request.PlanSku)) return $"Unsupported plan size '{request.PlanSku}'. Use one of: {string.Join(", ", SkuTiers.Keys)}.";
        var folderError = FolderRules.Validate(request.TargetFolder);
        return folderError;
    }

    /// <summary>Reads and validates the release agent's manifest. The agent's output is treated as untrusted input.</summary>
    public static DeployManifest ReadManifest(string targetFolder, string appPrefix)
    {
        var path = Path.Combine(targetFolder, ".etw", "deploy", "manifest.json");
        if (!File.Exists(path)) throw new InvalidOperationException("The release step did not write .etw/deploy/manifest.json, so there is nothing to deploy.");

        DeployManifest? manifest;
        try
        {
            manifest = JsonSerializer.Deserialize<DeployManifest>(File.ReadAllText(path), EtwJson.Options);
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException("manifest.json is not valid: " + ex.Message);
        }

        if (manifest is null || manifest.Components.Count is 0 or > 4) throw new InvalidOperationException("manifest.json must list between 1 and 4 components.");
        foreach (var c in manifest.Components)
        {
            if (!ComponentName().IsMatch(c.Name)) throw new InvalidOperationException($"Component name '{c.Name}' must be 2–20 lowercase letters or digits.");
            if ($"{appPrefix}-{c.Name}".Length > 60) throw new InvalidOperationException($"The web app name '{appPrefix}-{c.Name}' is longer than 60 characters.");
            if (!RuntimePrefixes.Any(p => c.Runtime.StartsWith(p, StringComparison.Ordinal))) throw new InvalidOperationException($"Runtime '{c.Runtime}' of {c.Name} is not supported (use DOTNETCORE|, NODE|, PYTHON|, JAVA| or PHP|).");
            if (c.StartupCommand is { Length: > 500 } || c.StartupCommand?.Any(char.IsControl) == true) throw new InvalidOperationException($"The startup command of {c.Name} is not valid.");

            var dir = ResolveInside(targetFolder, c.Directory);
            if (!Directory.Exists(dir) || !Directory.EnumerateFileSystemEntries(dir).Any()) throw new InvalidOperationException($"The folder '{c.Directory}' of {c.Name} is missing or empty.");
        }
        return manifest;
    }

    /// <summary>Resolves a manifest folder and makes sure it cannot point outside the project (no '..', no absolute paths).</summary>
    public static string ResolveInside(string root, string relative)
    {
        var rootFull = Path.GetFullPath(root).TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
        if (Path.IsPathRooted(relative)) throw new InvalidOperationException($"Deploy folder '{relative}' must be relative to the project.");
        var full = Path.GetFullPath(Path.Combine(rootFull, relative));
        if (!full.StartsWith(rootFull, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException($"Deploy folder '{relative}' points outside the project.");
        return full;
    }

    [GeneratedRegex(@"^[\w\-.()]{1,90}$")]
    private static partial Regex ResourceGroup();

    [GeneratedRegex("^[a-z0-9]{3,30}$")]
    private static partial Regex Location();

    [GeneratedRegex("^[a-z0-9][a-z0-9-]{1,38}[a-z0-9]$")]
    private static partial Regex AppPrefix();

    [GeneratedRegex("^[a-z0-9]{2,20}$")]
    private static partial Regex ComponentName();
}
