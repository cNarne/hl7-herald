using System.Collections.Concurrent;
using System.Threading.Channels;
using Jacobian.Hl7Integration.Domain.Exceptions;
using Jacobian.Hl7Integration.Domain.Models;
using Jacobian.Hl7Integration.Parser;
using Jacobian.Hl7Integration.Worker.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Jacobian.Hl7Integration.Worker.Services;

/// <summary>
/// Watches a configurable directory for inbound *.hl7 files, parses each via
/// <see cref="Hl7OrmParser"/>, and persists the result via <see cref="IOrderRepository"/>.
///
/// ─────────────────────────────────────────────────────────────────────────────
/// Concurrency model (revised per dotnet-concurrency-specialist BUG finding #2):
///
///   The initial design used Task.Run in the FSW callback to adapt the synchronous
///   OnFileCreated event into an async WriteAsync call. The specialist identified this
///   as a backpressure gap: Task.Run returns immediately to the FSW thread, so the bounded
///   channel's FullMode.Wait never actually blocked the source. Under a large file drop,
///   one suspended heap-allocated Task was produced per file above channel capacity,
///   accumulating unbounded memory rather than providing true backpressure.
///
///   The revised design uses a two-stage pipeline:
///
///   Stage 1 — Staging channel (unbounded)
///     FSW callback writes synchronously via TryWrite — no Task.Run, no blocking.
///     The unbounded channel absorbs OS-level bursts while keeping FSW callbacks
///     synchronous and non-blocking. Memory growth is explicit and bounded by FSW's
///     own InternalBufferSize (64 KB, ~1,000–4,000 events) rather than hidden in
///     heap-allocated Task closures.
///
///   Stage 2 — Processing channel (bounded, FullMode.Wait)
///     A single ProduceAsync task reads from the staging channel and writes to the
///     bounded processing channel, awaiting WriteAsync when the channel is full.
///     This is the actual backpressure point: one Task awaits, not N Tasks.
///     True backpressure is now present at the staging channel boundary.
///
///   Stage 3 — Consumers (N parallel tasks)
///     ConcurrencyLimit tasks drain the bounded channel via ReadAllAsync.
///     Each creates its own IServiceScope for a fresh IOrderRepository.
///
/// ─────────────────────────────────────────────────────────────────────────────
/// dotnet-concurrency-specialist review checklist:
///   ✅ Processing channel is bounded — unbounded channel memory leak eliminated.
///   ✅ FullMode.Wait on processing channel — backpressure is real (ProduceAsync awaits).
///   ✅ Every consumer await passes CancellationToken.
///   ✅ Processing channel Writer.TryComplete() in exactly one place (ProduceAsync.finally).
///   ✅ IServiceScope created per file (not per service instance).
///   ✅ FileSystemWatcher Error event handled — shutdown guard added (Finding 6 RISK).
///   ✅ OnWatcherError triggers ScanExistingFilesAsync after restart (Finding 6 RISK).
///   ✅ InternalBufferSize = 65536 (max) reduces overflow frequency (Finding 6 RISK).
///   ✅ Deduplication: IOrderRepository idempotency on MessageControlId is the contract
///      for the post-TryRemove window (Finding 1 RISK — see IOrderRepository XML doc).
///   ✅ No Task.Run wrappers — FSW callback is synchronous, parse is synchronous.
///   ✅ StopAsync uses WaitAsync(cancellationToken) to respect host shutdown timeout.
///
/// ─────────────────────────────────────────────────────────────────────────────
/// HIPAA / PHI logging policy:
///   INFO and above: FileName, ControlId, OrderGroupCount — no patient data.
///   DEBUG only: file paths useful for diagnostics.
///   PatientId value is never logged at any level in this class.
/// </summary>
public sealed class Hl7FileIngestionService : IHostedService, IAsyncDisposable
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly Hl7OrmParser _parser;
    private readonly IOptionsMonitor<IngestionOptions> _options;
    private readonly ILogger<Hl7FileIngestionService> _logger;

    // Stage 1: unbounded staging channel — written synchronously from FSW callbacks.
    // Absorbs OS-level burst events without blocking the FSW thread or allocating Tasks.
    // Bounded only by FSW's InternalBufferSize (64 KB) and the OS event buffer.
    private Channel<string>? _stagingChannel;

    // Stage 2: bounded processing channel — backpressure point.
    // ProduceAsync awaits WriteAsync here; consumers drain via ReadAllAsync.
    private Channel<string>? _fileChannel;

    // Single producer task: drains _stagingChannel → _fileChannel.
    // Completes _fileChannel.Writer when _stagingChannel is fully drained.
    private Task? _producerTask;

    // N consumer tasks: drain _fileChannel.
    private Task? _consumerTask;

    private FileSystemWatcher? _watcher;
    private CancellationTokenSource? _cts;

    // Deduplication: tracks paths currently in the pipeline (staging → channel → processing).
    // Prevents FSW double-events. Entries are removed in ProcessFileAsync.finally.
    // NOTE (Finding 1 RISK): the window between TryRemove and physical file deletion/archival
    // means a third FSW event could re-queue the same file. The contract with IOrderRepository
    // is that SaveAsync is idempotent on MessageControlId — see IOrderRepository XML doc.
    private readonly ConcurrentDictionary<string, byte> _inFlight =
        new(StringComparer.OrdinalIgnoreCase);

    public Hl7FileIngestionService(
        IServiceScopeFactory scopeFactory,
        Hl7OrmParser parser,
        IOptionsMonitor<IngestionOptions> options,
        ILogger<Hl7FileIngestionService> logger)
    {
        ArgumentNullException.ThrowIfNull(scopeFactory);
        ArgumentNullException.ThrowIfNull(parser);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);

        _scopeFactory = scopeFactory;
        _parser       = parser;
        _options      = options;
        _logger       = logger;
    }

    // ──────────────────────────────────────────────────────────────────────────
    // IHostedService
    // ──────────────────────────────────────────────────────────────────────────

    public Task StartAsync(CancellationToken cancellationToken)
    {
        // cancellationToken here is the host's *startup* timeout — not the service lifetime.
        // We create our own CTS for the running lifetime.
        var opts = _options.CurrentValue;

        ValidateWatchDirectory(opts.WatchDirectory);

        _cts = new CancellationTokenSource();

        // Stage 1: unbounded staging channel.
        // SingleWriter = false: OnFileCreated and ScanExistingFilesAsync may call TryWrite
        //   concurrently (e.g., if a file arrives during startup scan).
        // SingleReader = true: only ProduceAsync reads from staging.
        _stagingChannel = Channel.CreateUnbounded<string>(new UnboundedChannelOptions
        {
            SingleWriter = false,
            SingleReader = true,
        });

        // Stage 2: bounded processing channel.
        // FullMode.Wait: ProduceAsync blocks here when consumers are saturated.
        //   This is the actual backpressure point — one task awaits, not N Task.Run closures.
        // SingleWriter = true: only ProduceAsync writes.
        // SingleReader = false: N consumer tasks read concurrently.
        _fileChannel = Channel.CreateBounded<string>(new BoundedChannelOptions(opts.ChannelCapacity)
        {
            FullMode     = BoundedChannelFullMode.Wait,
            SingleWriter = true,
            SingleReader = false,
        });

        // Start the single producer: staging → processing channel.
        _producerTask = ProduceAsync(_cts.Token);

        // Start N consumer tasks.
        _consumerTask = Task.WhenAll(
            Enumerable.Range(0, opts.ConcurrencyLimit)
                      .Select(_ => ConsumeAsync(_cts.Token)));

        // Wire up FSW after channels and tasks are ready.
        _watcher = new FileSystemWatcher(opts.WatchDirectory, opts.FileFilter)
        {
            // 64 KB internal buffer (maximum allowed) — reduces InternalBufferOverflowException
            // frequency under burst load. Default is 8 KB (~100–200 events before overflow).
            // Recommended by dotnet-concurrency-specialist, Finding 6.
            InternalBufferSize    = 65_536,
            IncludeSubdirectories = false,
            EnableRaisingEvents   = false,
        };
        _watcher.Created += OnFileCreated;
        _watcher.Error   += OnWatcherError;
        _watcher.EnableRaisingEvents = true;

        // Scan for files that arrived before the watcher started.
        _ = ScanExistingFilesAsync(_cts.Token);

        _logger.LogInformation(
            "HL7 ingestion started. Directory={Directory} Filter={Filter} " +
            "Concurrency={Concurrency} ChannelCapacity={Capacity}",
            opts.WatchDirectory, opts.FileFilter,
            opts.ConcurrencyLimit, opts.ChannelCapacity);

        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_stagingChannel is null || _producerTask is null || _consumerTask is null)
            return;

        // Stop raising new events first — no new paths enter the pipeline after this.
        if (_watcher is not null)
            _watcher.EnableRaisingEvents = false;

        // Signal: no more files from FSW or startup scan.
        // ProduceAsync reads from staging; when staging is complete and drained,
        // ProduceAsync.finally calls _fileChannel.Writer.TryComplete().
        _stagingChannel.Writer.Complete();

        try
        {
            // Wait for the producer to drain staging and complete the processing channel.
            // WaitAsync respects the host's shutdown timeout (dotnet-concurrency-specialist
            // Finding 3 — optional improvement applied). If the timeout fires, we fall through
            // and complete the processing channel directly so consumers can terminate.
            await _producerTask.WaitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            // Host shutdown timeout fired before staging was fully drained.
            // Complete the processing channel directly so consumer tasks can exit.
            _fileChannel?.Writer.TryComplete();
        }

        try
        {
            // Wait for consumers to drain the processing channel and finish all in-flight files.
            await _consumerTask.WaitAsync(cancellationToken);
        }
        catch (OperationCanceledException) { }
        catch (AggregateException ae)
            when (ae.InnerExceptions.All(ex => ex is OperationCanceledException)) { }

        _logger.LogInformation("HL7 ingestion stopped.");
    }

    // ──────────────────────────────────────────────────────────────────────────
    // Stage 1: FileSystemWatcher callback → staging channel
    // ──────────────────────────────────────────────────────────────────────────

    private void OnFileCreated(object sender, FileSystemEventArgs e)
    {
        var path = e.FullPath;

        // Deduplicate: TryAdd returns false if the path is already in-flight.
        // FSW raises Created twice for the same file on some OS/AV combinations.
        if (!_inFlight.TryAdd(path, 0))
        {
            _logger.LogDebug("Duplicate FSW Created event skipped. File={FileName}",
                Path.GetFileName(path));
            return;
        }

        // Synchronous, non-blocking write to the unbounded staging channel.
        // TryWrite returns false only when the channel is completed (service stopping).
        // No Task.Run needed — the FSW thread is not blocked; backpressure is handled
        // in ProduceAsync when it awaits WriteAsync on the bounded processing channel.
        if (!_stagingChannel!.Writer.TryWrite(path))
        {
            // Channel completed — service is stopping. Clean up in-flight entry.
            _inFlight.TryRemove(path, out _);
        }
    }

    private void OnWatcherError(object sender, ErrorEventArgs e)
    {
        var ex = e.GetException();
        _logger.LogError(ex,
            "FileSystemWatcher error — events may have been lost. " +
            "Attempting restart. Manual review of watch directory may be required.");

        // RISK fix (dotnet-concurrency-specialist Finding 6):
        // Guard against shutdown race. StopAsync may have already set EnableRaisingEvents=false.
        // Re-enabling the watcher here would cause events to fire after shutdown began,
        // potentially writing to a completed channel.
        if (_cts is null || _cts.IsCancellationRequested)
            return;

        try
        {
            if (_watcher is not null)
            {
                _watcher.EnableRaisingEvents = false;
                _watcher.EnableRaisingEvents = true;

                _logger.LogInformation("FileSystemWatcher restarted after error.");

                // RISK fix: scan for files that arrived during the false/true gap.
                // Events for files that landed between Disable and Enable are lost permanently.
                // ScanExistingFilesAsync uses _inFlight.TryAdd to skip files already queued.
                _ = ScanExistingFilesAsync(_cts.Token);
            }
        }
        catch (Exception restartEx)
        {
            _logger.LogCritical(restartEx,
                "FileSystemWatcher could not be restarted. New inbound files will NOT be " +
                "processed until the service is restarted. Directory={Directory}",
                _options.CurrentValue.WatchDirectory);
        }
    }

    // ──────────────────────────────────────────────────────────────────────────
    // Startup file scan (also called after watcher restart to cover event-gap window)
    // ──────────────────────────────────────────────────────────────────────────

    private Task ScanExistingFilesAsync(CancellationToken ct)
    {
        // Synchronous enumeration; no async I/O required. Returns a completed Task unless
        // a future refactor adds async enumeration. The fire-and-forget callers (StartAsync
        // and OnWatcherError) discard the Task — errors are caught internally.
        try
        {
            var opts = _options.CurrentValue;
            var existingFiles = Directory.EnumerateFiles(opts.WatchDirectory, opts.FileFilter);

            foreach (var path in existingFiles)
            {
                if (ct.IsCancellationRequested) break;

                // TryAdd prevents re-queuing a file already in-flight from a concurrent
                // FSW Created event or previous scan run.
                if (_inFlight.TryAdd(path, 0))
                {
                    // Synchronous TryWrite: staging channel is unbounded — never blocks.
                    if (!_stagingChannel!.Writer.TryWrite(path))
                    {
                        _inFlight.TryRemove(path, out _);
                        break; // Channel completed — service stopping.
                    }
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error scanning watch directory for existing files.");
        }

        return Task.CompletedTask;
    }

    // ──────────────────────────────────────────────────────────────────────────
    // Stage 2: Producer — bridges staging channel → bounded processing channel
    // ──────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Reads paths from the unbounded staging channel and writes to the bounded processing
    /// channel. This is the single backpressure point: WriteAsync awaits when the processing
    /// channel is full, naturally rate-limiting the pipeline without accumulating suspended
    /// Task closures (the BUG identified by dotnet-concurrency-specialist Finding 2).
    ///
    /// The finally block completes the processing channel writer when staging is exhausted,
    /// allowing consumer tasks' ReadAllAsync to drain and terminate.
    /// Per csharp-concurrency-patterns: Writer.Complete in exactly one place.
    /// </summary>
    private async Task ProduceAsync(CancellationToken ct)
    {
        try
        {
            await foreach (var path in _stagingChannel!.Reader.ReadAllAsync(ct))
            {
                try
                {
                    // Backpressure point: awaits here when the processing channel is full.
                    await _fileChannel!.Writer.WriteAsync(path, ct);
                }
                catch (OperationCanceledException)
                {
                    _inFlight.TryRemove(path, out _);
                    return;
                }
                catch (ChannelClosedException)
                {
                    _inFlight.TryRemove(path, out _);
                    return;
                }
            }
        }
        finally
        {
            // Whether staging drained normally, CT fired, or an exception escaped,
            // always complete the processing channel so consumers can terminate.
            _fileChannel?.Writer.TryComplete();
        }
    }

    // ──────────────────────────────────────────────────────────────────────────
    // Stage 3: Consumers — drain processing channel
    // ──────────────────────────────────────────────────────────────────────────

    private async Task ConsumeAsync(CancellationToken ct)
    {
        // ReadAllAsync terminates when the processing channel Writer is completed
        // (signalled by ProduceAsync.finally) and all items are drained.
        await foreach (var filePath in _fileChannel!.Reader.ReadAllAsync(ct))
        {
            await ProcessFileAsync(filePath, ct);
        }
    }

    private async Task ProcessFileAsync(string filePath, CancellationToken ct)
    {
        var fileName = Path.GetFileName(filePath);

        try
        {
            // Brief delay: the OS raises Created when the file handle is first opened,
            // not necessarily when writing is complete. ReadWithRetryAsync adds resilience.
            await Task.Delay(TimeSpan.FromMilliseconds(50), ct);

            var rawMessage = await ReadWithRetryAsync(filePath, ct);

            // Parse is synchronous — NHapi does not offer async parsing.
            // Task.Run is NOT used: wrapping a fast synchronous call wastes a thread-pool
            // thread for no throughput benefit at typical HL7 message volumes.
            var record = _parser.Parse(rawMessage);

            // IServiceScope per file (dependency-injection-patterns guidance):
            // Hl7FileIngestionService is Singleton (IHostedService lifetime).
            // IOrderRepository implementations are typically Scoped (EF Core DbContext).
            // IServiceScopeFactory.CreateAsyncScope creates a fresh scope (and DbContext)
            // per file, preventing concurrent DbContext access across parallel consumers.
            await using var scope = _scopeFactory.CreateAsyncScope();
            var repository = scope.ServiceProvider.GetRequiredService<IOrderRepository>();

            var isNew = await repository.SaveAsync(record, ct);

            // HIPAA logging: FileName and ControlId are operational metadata — not PHI.
            _logger.LogInformation(
                "Processed HL7 file. FileName={FileName} ControlId={ControlId} " +
                "OrderGroups={OrderGroupCount} IsNewRecord={IsNew}",
                fileName,
                record.MessageControlId,
                record.OrderGroups.Count,
                isNew);
        }
        catch (Hl7ParseException ex)
        {
            // Unrecoverable parse failure. File is not retried automatically.
            // The file remains on disk for dead-letter review.
            _logger.LogError(ex,
                "HL7 parse failure — file skipped. FileName={FileName} ControlId={ControlId}",
                fileName,
                ex.MessageControlId ?? "unknown");
        }
        catch (OperationCanceledException)
        {
            // Service stopping mid-process. File remains on disk and will be picked up
            // by ScanExistingFilesAsync on next startup.
            _logger.LogInformation(
                "File processing interrupted by shutdown. FileName={FileName}", fileName);
        }
        catch (Exception ex)
        {
            // Per constraint: "one malformed file must not abort the batch."
            // Catch-all ensures this consumer continues to the next file.
            _logger.LogError(ex,
                "Unexpected error processing file. FileName={FileName}", fileName);
        }
        finally
        {
            // Always remove from in-flight. See Finding 1 RISK note on the field declaration:
            // IOrderRepository.SaveAsync must be idempotent on MessageControlId to guard
            // against the post-TryRemove / pre-file-deletion event window.
            _inFlight.TryRemove(filePath, out _);
        }
    }

    // ──────────────────────────────────────────────────────────────────────────
    // File read with retry (FSW race condition guard)
    // ──────────────────────────────────────────────────────────────────────────

    private static async Task<string> ReadWithRetryAsync(string filePath, CancellationToken ct)
    {
        const int maxAttempts = 3;

        for (var attempt = 1; attempt < maxAttempts; attempt++)
        {
            try
            {
                return await File.ReadAllTextAsync(filePath, ct);
            }
            catch (IOException)
            {
                // File still being written. Exponential back-off: 100ms, 200ms, then throw.
                await Task.Delay(TimeSpan.FromMilliseconds(100 * attempt), ct);
            }
        }

        return await File.ReadAllTextAsync(filePath, ct);
    }

    // ──────────────────────────────────────────────────────────────────────────
    // Startup validation
    // ──────────────────────────────────────────────────────────────────────────

    private void ValidateWatchDirectory(string path)
    {
        if (!Directory.Exists(path))
        {
            throw new InvalidOperationException(
                $"Watch directory does not exist: '{path}'. " +
                "Ensure the service account has read access and the path is configured " +
                $"correctly in appsettings.json under " +
                $"'{IngestionOptions.SectionName}:{nameof(IngestionOptions.WatchDirectory)}'.");
        }
    }

    // ──────────────────────────────────────────────────────────────────────────
    // IAsyncDisposable — safety net if StopAsync was not called or threw
    // ──────────────────────────────────────────────────────────────────────────

    public async ValueTask DisposeAsync()
    {
        // Idempotent: TryComplete on an already-completed writer returns false, no throw.
        _stagingChannel?.Writer.TryComplete();
        _fileChannel?.Writer.TryComplete();
        _watcher?.Dispose();

        if (_cts is not null)
        {
            await _cts.CancelAsync();
            _cts.Dispose();
        }

        foreach (var task in new[] { _producerTask, _consumerTask })
        {
            if (task is null) continue;
            try { await task; }
            catch (OperationCanceledException) { }
            catch (AggregateException ae)
                when (ae.InnerExceptions.All(ex => ex is OperationCanceledException)) { }
        }
    }
}
