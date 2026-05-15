using Jacobian.Hl7Integration.Domain.Models;

namespace Jacobian.Hl7Integration.Worker.Services;

/// <summary>
/// Persistence contract for parsed order records.
///
/// Implementations are responsible for:
///   1. Idempotency: if a record with the same <see cref="OrderRecord.MessageControlId"/>
///      already exists, the implementation must not create a duplicate. The recommended
///      strategy is an upsert on MessageControlId.
///   2. Atomicity: <see cref="OrderRecord.RawHl7Message"/> and all OrderGroup rows must
///      be written in a single transaction. A partial write (header without groups, or
///      groups without the raw message) must not be committed.
///   3. PHI: the <see cref="OrderRecord.RawHl7Message"/> column must be treated as PHI.
///      Implementations targeting SQL Server should use column-level encryption or
///      TDE with Encrypt=true in the connection string.
///
/// Lifetime: register implementations as Scoped. Each Hl7FileIngestionService consumer
/// task resolves a fresh scope so that EF Core DbContext instances (if used) are not
/// shared across concurrent file processing.
/// </summary>
public interface IOrderRepository
{
    /// <summary>
    /// Persists an <see cref="OrderRecord"/> and all its <see cref="OrderGroup"/> children.
    /// </summary>
    /// <param name="record">The fully parsed record to persist.</param>
    /// <param name="cancellationToken">Cancellation token for the write operation.</param>
    /// <returns>
    /// True if the record was newly inserted; false if it already existed
    /// (idempotent re-delivery of the same MSH-10 control ID).
    /// </returns>
    Task<bool> SaveAsync(OrderRecord record, CancellationToken cancellationToken = default);
}
