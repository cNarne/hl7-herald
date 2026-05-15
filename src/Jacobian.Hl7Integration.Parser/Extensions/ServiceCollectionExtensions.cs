using Jacobian.Hl7Integration.Parser;
using Microsoft.Extensions.DependencyInjection;

namespace Jacobian.Hl7Integration.Parser.Extensions;

/// <summary>
/// DI registration for the parser assembly.
///
/// Lifetime rationale for Hl7OrmParser → Transient:
///   Hl7OrmParser holds no state beyond its ILogger dependency. A new PipeParser is
///   created per Parse() call to guarantee thread-safety (NHapi PipeParser is not
///   thread-safe for concurrent calls). Transient is correct: each consumer (file
///   ingestion worker, MLLP listener, etc.) gets its own instance, and there is no
///   shared mutable state to protect.
///
///   Scoped would also be correct in a scoped context (e.g., per-request in a web host),
///   but this service runs as a hosted background service with no HTTP request scope.
///   Transient avoids needing to reason about scope lifetimes at all.
///
///   Singleton would require either locking Parse() (serializing all parsing) or
///   ThreadLocal{PipeParser} — both more complex than Transient for no measurable gain
///   at expected message volumes.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="Hl7OrmParser"/> as Transient.
    /// </summary>
    public static IServiceCollection AddHl7OrmParser(this IServiceCollection services)
    {
        services.AddTransient<Hl7OrmParser>();
        return services;
    }
}
