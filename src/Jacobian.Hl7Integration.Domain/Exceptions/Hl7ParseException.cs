namespace Jacobian.Hl7Integration.Domain.Exceptions;

/// <summary>
/// Thrown when an HL7 message cannot be parsed and the failure is unrecoverable.
///
/// Callers that catch this exception should:
///   1. Send an HL7 ACK with MSA-1 = "AR" (Application Reject) or "AE" (Application Error).
///   2. Log <see cref="MessageControlId"/> for audit. If MSH-10 could not be extracted
///      before the failure, <see cref="MessageControlId"/> is null — the ACK MSA-2 should
///      echo back whatever partial control ID was recoverable.
///   3. Write the raw message to a dead-letter store for manual review.
///
/// This exception is not thrown for messages that parse successfully but contain unexpected
/// or missing optional fields — those cases return an <c>OrderRecord</c> with null fields.
/// It is only thrown when parsing cannot continue at all (malformed MSH, missing required
/// segment, or unresolvable structural error).
/// </summary>
public sealed class Hl7ParseException : Exception
{
    /// <summary>
    /// MSH-10 Message Control ID extracted before the failure, if available.
    /// May be null if MSH itself was malformed.
    /// </summary>
    public string? MessageControlId { get; }

    /// <summary>
    /// MSH-3 Sending Application, if extractable before the failure.
    /// Used to route NAK responses back to the correct sending system.
    /// </summary>
    public string? SendingApplication { get; }

    public Hl7ParseException(
        string message,
        string? messageControlId = null,
        string? sendingApplication = null)
        : base(message)
    {
        MessageControlId = messageControlId;
        SendingApplication = sendingApplication;
    }

    public Hl7ParseException(
        string message,
        Exception innerException,
        string? messageControlId = null,
        string? sendingApplication = null)
        : base(message, innerException)
    {
        MessageControlId = messageControlId;
        SendingApplication = sendingApplication;
    }
}
