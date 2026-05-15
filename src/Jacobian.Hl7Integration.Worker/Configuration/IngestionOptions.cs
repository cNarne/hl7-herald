using System.ComponentModel.DataAnnotations;

namespace Jacobian.Hl7Integration.Worker.Configuration;

/// <summary>
/// Configuration for <see cref="Services.Hl7FileIngestionService"/>.
/// Bound from the "Ingestion" section of appsettings.json via IOptions{IngestionOptions}.
///
/// No magic strings, no Environment.GetEnvironmentVariable in production code.
/// All settings are surfaced here and validated at startup via ValidateDataAnnotations()
/// so misconfiguration fails fast rather than at the first file arrival.
/// </summary>
public sealed class IngestionOptions
{
    /// <summary>
    /// appsettings.json section key used for binding.
    /// </summary>
    public const string SectionName = "Ingestion";

    /// <summary>
    /// Absolute path of the directory to watch for inbound *.hl7 files.
    /// The service account running this worker must have read + delete access.
    /// </summary>
    [Required]
    [MinLength(1)]
    public string WatchDirectory { get; set; } = string.Empty;

    /// <summary>
    /// Maximum number of *.hl7 files processed concurrently.
    /// Each file occupies one slot until its OrderRecord is written to the repository.
    ///
    /// Sizing guidance:
    ///   - Set to (CPU count) for CPU-bound workloads.
    ///   - Set to 2–4× CPU count for I/O-bound workloads (file read + DB write).
    ///   - Do not exceed the SQL Server connection pool size (default: 100).
    ///
    /// Defaults to 4, which is conservative and appropriate for a single SQL Server target.
    /// </summary>
    [Range(1, 64)]
    public int ConcurrencyLimit { get; set; } = 4;

    /// <summary>
    /// Bounded channel capacity. When the channel is full (more files arrive than the
    /// consumers can process), the producer (FileSystemWatcher callback) blocks until
    /// a slot opens. This provides natural backpressure — the OS file buffer absorbs
    /// burst arrivals rather than unbounded in-memory queuing.
    ///
    /// Set to at least ConcurrencyLimit × 2. Defaults to 32.
    /// </summary>
    [Range(1, 1024)]
    public int ChannelCapacity { get; set; } = 32;

    /// <summary>
    /// File glob filter passed to FileSystemWatcher.Filter.
    /// Defaults to "*.hl7". Change to "*.txt" if the sending system uses a different
    /// extension — do not rely on content sniffing.
    /// </summary>
    [Required]
    public string FileFilter { get; set; } = "*.hl7";
}
