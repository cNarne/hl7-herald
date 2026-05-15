namespace Jacobian.Hl7Integration.Domain.Models;

// ---------------------------------------------------------------------------
// Value type decision (type-design-performance):
//
// Hl7Identifier is modeled as a readonly record struct because:
//   1. It is semantically a value — (ID + type code) — not an entity.
//   2. Two Hl7Identifiers are equal when both fields match; value equality is correct.
//   3. The struct header is stack-allocated in local scope, avoiding a heap allocation
//      for the wrapper object when iterating PID-3 candidates.
//
// Caveat: both fields are string references, so the string data itself is still heap-
// allocated. The win is struct-copy value semantics and no boxing in generic collections
// that are constrained to IEquatable<T>. For large identifier arrays (rare in HL7 PID-3,
// which typically carries 2–4 repetitions) an ArrayPool<Hl7Identifier> strategy could
// eliminate remaining pressure, but that is premature at current volumes.
//
// OrderRecord and OrderGroup are sealed reference records because:
//   - They contain multiple reference-type fields; struct copying would be expensive.
//   - sealed prevents accidental subclass construction that bypasses required-init.
//   - Positional record syntax is intentionally avoided: the number of fields exceeds
//     what positional syntax handles readably, and named construction is clearer.
// ---------------------------------------------------------------------------

/// <summary>
/// An HL7 CX (composite ID) representing a single patient identifier repetition.
/// Selected from PID-3 using the MR → PI → first-available precedence rule.
/// </summary>
public readonly record struct Hl7Identifier(string Value, string? TypeCode);

/// <summary>
/// A fully parsed HL7 v2 ORM^O01 message.
///
/// Invariants:
///   - <see cref="RawHl7Message"/> is set before any field extraction begins. It is
///     always populated, even when downstream parsing fails partially.
///   - <see cref="MessageControlId"/> is the HL7 MSH-10 value used for ACK/NAK responses
///     and idempotency checks. It is required — messages without it are rejected.
///   - <see cref="OrderGroups"/> contains one entry per ORC/OBR pair. A single
///     ORM^O01 message may legitimately carry 1..N order groups.
///
/// HIPAA note: Do not pass this object to ILogger structured logging without a
/// destructuring policy that redacts PatientId, PatientName, and DateOfBirth.
/// Use log properties (FileName, OrderId, PatientIdType) at INFO and above — never
/// the values of PHI fields.
/// </summary>
public sealed record OrderRecord
{
    /// <summary>
    /// The raw pipe-delimited HL7 string, normalized to \r segment terminators.
    /// Stored before any parsing begins. Populated even when parsing fails partially.
    /// Essential for audit trails, NAK replay, and incident investigation.
    /// </summary>
    public required string RawHl7Message { get; init; }

    /// <summary>MSH-10: Message Control ID. Used for ACK/NAK and idempotency.</summary>
    public required string MessageControlId { get; init; }

    /// <summary>MSH-3: Sending Application (HD.NamespaceID). Empty string if absent.</summary>
    public required string SendingApplication { get; init; }

    /// <summary>MSH-4: Sending Facility (HD.NamespaceID).</summary>
    public string? SendingFacility { get; init; }

    /// <summary>
    /// MSH-9 formatted as "MessageCode^TriggerEvent" (e.g., "ORM^O01").
    /// Component 3 (message structure) is intentionally omitted — many systems send
    /// only two components, and the two-component form is sufficient for routing.
    /// </summary>
    public string? MessageType { get; init; }

    /// <summary>
    /// MSH-7: Date/Time of message.
    /// Stored as DateTimeOffset to preserve the sending site's timezone offset.
    /// DTM strings without a timezone component are treated as UTC (offset +00:00).
    /// </summary>
    public DateTimeOffset? MessageDateTime { get; init; }

    /// <summary>
    /// PID-3: Patient identifier selected via MR → PI → first-available.
    /// Always present for valid ORM^O01 messages; parser throws if absent.
    /// </summary>
    public required string PatientId { get; init; }

    /// <summary>
    /// PID-3 CX.5: Identifier type code of the selected patient ID (e.g., "MR", "PI").
    /// Null when the source message omits the type code component.
    /// </summary>
    public string? PatientIdType { get; init; }

    /// <summary>
    /// PID-5: Patient name formatted as "LastName^FirstName^MiddleName".
    /// HL7 escape sequences (e.g., \S\ for ^) are decoded by NHapi before this value
    /// is set — the stored string contains literal characters, not escape sequences.
    /// </summary>
    public string? PatientName { get; init; }

    /// <summary>
    /// PID-7: Date of birth.
    /// Time component is typically absent (yyyyMMdd only); stored with time 00:00:00+00:00.
    /// </summary>
    public DateTimeOffset? DateOfBirth { get; init; }

    /// <summary>
    /// One entry per ORC/OBR order group in the message.
    ///
    /// Critical: LegacyHl7Parser silently dropped all but the last order group on multi-order
    /// messages (Bug 4 in hl7-migration-evaluation.md). This model fixes that defect by
    /// capturing every group. A lab order for CBC + BMP + UA in one message produces
    /// OrderGroups.Count == 3.
    /// </summary>
    public IReadOnlyList<OrderGroup> OrderGroups { get; init; } = [];
}

/// <summary>
/// A single ORC/OBR order group within an ORM^O01 message.
///
/// An ORC segment is always present. An OBR segment is optional (ORC-only orders are valid,
/// e.g., order cancellations). <see cref="HasObrSegment"/> indicates whether OBR data is
/// meaningful on this group.
/// </summary>
public sealed record OrderGroup
{
    /// <summary>
    /// ORC-1: Order Control Code. Always present; parser throws if absent.
    /// Common values: NW (New), CA (Cancel), SC (Status Change), DC (Discontinue),
    /// RP (Replace), HD (Hold), RL (Release), OD (Order Detail Void).
    /// </summary>
    public required string OrderControlCode { get; init; }

    /// <summary>ORC-2 EI.1: Placer Order Number (the sending application's order ID).</summary>
    public string? OrderId { get; init; }

    /// <summary>ORC-3 EI.1: Filler Order Number (the receiving application's order ID).</summary>
    public string? FillerOrderId { get; init; }

    /// <summary>OBR-4 CWE.1: Universal Service Identifier code (e.g., "85025" for CBC).</summary>
    public string? UniversalServiceId { get; init; }

    /// <summary>OBR-4 CWE.2: Universal Service Identifier display text (e.g., "CBC WITH DIFF").</summary>
    public string? UniversalServiceText { get; init; }

    /// <summary>OBR-4 CWE.3: Coding system for the service identifier (e.g., "LN" for LOINC).</summary>
    public string? UniversalServiceCodingSystem { get; init; }

    /// <summary>
    /// Ordered date/time resolved from ORC-9 (Date/Time of Transaction) with fallback
    /// to OBR-7 (Observation Date/Time).
    ///
    /// OBR-6 (Requested Date/Time) is NEVER read. It was deprecated in HL7 v2.3 and is
    /// almost always empty or zero-filled in real-world systems. Reading it produced null
    /// OrderedDateTime in the legacy parser for the majority of production messages.
    /// </summary>
    public DateTimeOffset? OrderedDateTime { get; init; }

    /// <summary>
    /// ORC-12 / OBR-16: Ordering provider formatted as "LastName^FirstName^Degree".
    /// ORC-12 takes precedence; OBR-16 is the fallback.
    /// </summary>
    public string? OrderingProviderName { get; init; }

    /// <summary>OBR-5 (deprecated) / priority from ORC-7 TQ.6: R=Routine, S=Stat, A=ASAP, P=Preoperative.</summary>
    public string? Priority { get; init; }

    /// <summary>
    /// True when an OBR segment was present and had a non-empty Universal Service ID.
    /// False for ORC-only order groups (e.g., cancellation messages without OBR detail).
    /// When false, UniversalServiceId, UniversalServiceText, and related OBR fields are null.
    /// </summary>
    public bool HasObrSegment { get; init; }
}
