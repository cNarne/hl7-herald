using Jacobian.Hl7Integration.Parser.Extensions;
using Jacobian.Hl7Integration.Worker.Configuration;
using Jacobian.Hl7Integration.Worker.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Jacobian.Hl7Integration.Worker.Extensions;

/// <summary>
/// DI registration for the Worker assembly.
///
/// ──────────────────────────────────────────────────────────────────────────────
/// Lifetime rationale (per dependency-injection-patterns):
///
///   Hl7FileIngestionService → AddHostedService (singleton by IHostedService contract)
///     The service is a background worker with no per-request scope. Singleton is the
///     only valid lifetime for IHostedService implementations. State is limited to the
///     channel, watcher, and CTS — all created in StartAsync and torn down in StopAsync.
///
///   IOrderRepository → Scoped (see IOrderRepository XML doc)
///     Implementations use EF Core DbContext, which must be Scoped. Consumers in
///     Hl7FileIngestionService resolve IOrderRepository via IServiceScopeFactory per file,
///     so no scoped-into-singleton anti-pattern occurs.
///
///   IngestionOptions → ValidateDataAnnotations + ValidateOnStart
///     Per microsoft-extensions-configuration: validate at startup so misconfiguration
///     fails fast rather than at the first file arrival. A missing WatchDirectory would
///     otherwise surface as a FileSystemWatcher exception only when the first *.hl7 file
///     arrives in production.
///
/// ──────────────────────────────────────────────────────────────────────────────
/// NOTE: IOrderRepository is registered here as a placeholder comment only.
/// The concrete implementation lives in the persistence layer (not yet part of this
/// project). Uncomment and replace the line below when the implementation exists:
///
///   services.AddScoped{IOrderRepository, SqlOrderRepository}();
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers the HL7 worker: parser, hosted service, and configuration with startup validation.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configuration">The application configuration (for binding IngestionOptions).</param>
    public static IServiceCollection AddHl7Worker(
        this IServiceCollection services,
        Microsoft.Extensions.Configuration.IConfiguration configuration)
    {
        // Parser (Transient — see Jacobian.Hl7Integration.Parser.Extensions for rationale)
        services.AddHl7OrmParser();

        // Configuration — per microsoft-extensions-configuration best practice:
        // ValidateDataAnnotations checks [Required] and [Range] attributes on IngestionOptions.
        // ValidateOnStart surfaces misconfiguration at startup, not at first use.
        // IOptionsMonitor<T> is used in Hl7FileIngestionService so it receives live config
        // reloads (e.g., a concurrency limit change without service restart).
        services
            .AddOptions<IngestionOptions>()
            .BindConfiguration(IngestionOptions.SectionName)
            .ValidateDataAnnotations()
            .ValidateOnStart();

        // Hosted service — singleton lifetime by IHostedService contract.
        services.AddHostedService<Hl7FileIngestionService>();

        return services;
    }
}
