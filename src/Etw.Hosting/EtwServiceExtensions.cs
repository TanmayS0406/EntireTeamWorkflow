using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Etw.Shared;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Etw.Hosting;

/// <summary>The shared secret every service and the desktop app send in <see cref="EtwConstants.ApiKeyHeader"/>.</summary>
public sealed record ApiKey(string Value);

public static class EtwServiceExtensions
{
    /// <summary>
    /// Common setup for every Etw service: runs as a Windows service when installed as one (and as a console app
    /// otherwise), listens on loopback only, logs to %ProgramData%\EntireTeamWorkflow\logs and reads the API key.
    /// </summary>
    public static WebApplicationBuilder AddEtwService(this WebApplicationBuilder builder, string serviceName, int port)
    {
        builder.Host.UseWindowsService(options => options.ServiceName = serviceName);

        // An explicit --urls / ASPNETCORE_URLS still wins, which the integration tests rely on.
        if (string.IsNullOrEmpty(builder.Configuration["urls"]))
        {
            builder.WebHost.UseUrls(EtwConstants.Url(port));
        }

        builder.Logging.AddProvider(new FileLoggerProvider(serviceName));
        builder.Services.AddSingleton(new ApiKey(ApiKeyStore.GetOrCreate()));

        builder.Services.ConfigureHttpJsonOptions(options =>
        {
            options.SerializerOptions.Converters.Add(new JsonStringEnumConverter());
            options.SerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
        });
        return builder;
    }

    /// <summary>Rejects every request that does not carry the API key (except <c>/health</c>).</summary>
    public static WebApplication UseEtwApiKey(this WebApplication app)
    {
        var expected = Encoding.UTF8.GetBytes(app.Services.GetRequiredService<ApiKey>().Value);
        app.Use(async (context, next) =>
        {
            if (context.Request.Path.StartsWithSegments("/health"))
            {
                await next();
                return;
            }

            var supplied = context.Request.Headers[EtwConstants.ApiKeyHeader].ToString();
            if (!CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(supplied), expected))
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                await context.Response.WriteAsJsonAsync(new { error = "Missing or invalid " + EtwConstants.ApiKeyHeader + " header." });
                return;
            }
            await next();
        });
        return app;
    }

    /// <summary>Registers a named HttpClient that talks to another Etw service and sends the API key.</summary>
    public static IHttpClientBuilder AddEtwClient(this IServiceCollection services, string name, string baseUrl, TimeSpan? timeout = null)
    {
        return services.AddHttpClient(name, (sp, client) =>
        {
            client.BaseAddress = new Uri(baseUrl);
            client.Timeout = timeout ?? TimeSpan.FromSeconds(100);
            client.DefaultRequestHeaders.Add(EtwConstants.ApiKeyHeader, sp.GetRequiredService<ApiKey>().Value);
        });
    }

    public static JsonSerializerOptions Json => EtwJson.Options;
}
