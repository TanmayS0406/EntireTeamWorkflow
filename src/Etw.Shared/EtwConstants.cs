using System.Text.Json;
using System.Text.Json.Serialization;

namespace Etw.Shared;

/// <summary>Ports, header names and service names shared by every process in the solution.</summary>
public static class EtwConstants
{
    public const string ApiKeyHeader = "X-Etw-Key";

    public const int BackendPort = 5180;
    public const int IntegrationsPort = 5181;
    public const int OrchestratorPort = 5182;
    public const int AzurePort = 5183;

    public const string BackendService = "EtwBackend";
    public const string IntegrationsService = "EtwIntegrations";
    public const string OrchestratorService = "EtwOrchestrator";
    public const string AzureService = "EtwAzure";

    public static string Url(int port) => $"http://127.0.0.1:{port}";

    public static string BackendUrl => Url(BackendPort);
    public static string IntegrationsUrl => Url(IntegrationsPort);
    public static string OrchestratorUrl => Url(OrchestratorPort);
    public static string AzureUrl => Url(AzurePort);
}

/// <summary>Where the services keep their data. Everything lives under %ProgramData%\EntireTeamWorkflow.</summary>
public static class EtwPaths
{
    public static string Root { get; } = Environment.GetEnvironmentVariable("ETW_DATA_DIR")
        is { Length: > 0 } custom
        ? custom
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "EntireTeamWorkflow");

    public static string Data => Ensure(Path.Combine(Root, "data"));
    public static string Logs => Ensure(Path.Combine(Root, "logs"));
    public static string Secrets => Ensure(Path.Combine(Root, "secrets"));
    public static string Temp => Ensure(Path.Combine(Root, "temp"));
    public static string ApiKeyFile => Path.Combine(Root, "api.key");

    private static string Ensure(string path)
    {
        Directory.CreateDirectory(path);
        return path;
    }
}

/// <summary>JSON options used on the wire by every service and by the desktop client.</summary>
public static class EtwJson
{
    public static JsonSerializerOptions Options { get; } = Create();

    public static JsonSerializerOptions Create()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        };
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }
}
