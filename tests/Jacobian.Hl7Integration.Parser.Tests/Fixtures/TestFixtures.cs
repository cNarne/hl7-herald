namespace Jacobian.Hl7Integration.Parser.Tests.Fixtures;

/// <summary>
/// Shared HL7 v2 ORM^O01 test message fixtures.
///
/// All segment terminators are the HL7-specified \r (CR, 0x0D).
/// The const strings use verbatim string literals with explicit \r escape codes
/// so the delimiter is visible and intentional, not an accident of the editor's line ending.
///
/// Variants are defined inline as static readonly strings (not separate methods) so that
/// xUnit [InlineData] can reference them and the change from the canonical fixture is
/// annotated in a comment immediately above the variant.
///
/// ─────────────────────────────────────────────────────────────────────────────
/// Primary fixture expected values (Happy Path / two-order-group test):
///   SendingApplication  = "OrderSystem"         (MSH-3.1)
///   MessageType         = "ORM^O01"              (MSH-9.1^MSH-9.2)
///   PatientId           = "123456"               (PID-3 CX.1, MR type selected)
///   PatientIdType       = "MR"                   (PID-3 CX.5)
///   PatientName         = "DOE^JOHN^A"           (PID-5 XPN reconstructed)
///   MessageControlId    = "MSG001"               (MSH-10)
///   OrderGroups.Count   = 1 (HappyPath) / 2 (TwoOrderGroups)
///   OrderGroups[0].OrderControlCode = "NW"
///   OrderGroups[0].OrderId          = "ORD-7890"
///   OrderGroups[0].UniversalServiceId = "85025"
///   OrderGroups[0].OrderedDateTime  = DateTimeOffset(2024,3,15,12,0,0, TimeSpan.Zero)
///                                     (from ORC-9 "20240315120000" — NOT OBR-6)
/// ─────────────────────────────────────────────────────────────────────────────
/// </summary>
public static class TestFixtures
{
    /// <summary>
    /// Single ORC/OBR order group. This is the canonical reference fixture.
    /// All variant comments describe the deviation from this string.
    /// </summary>
    public static readonly string HappyPathMessage =
        "MSH|^~\\&|OrderSystem|HospitalA|LabSystem|HospitalB|20240315120000||ORM^O01|MSG001|P|2.4|||AL|NE\r" +
        "PID|1||123456^^^HospitalA^MR||DOE^JOHN^A||19800101|M\r" +
        "ORC|NW|ORD-7890|||||^^^20240315||20240315120000|||PROVIDER^JANE^MD\r" +
        "OBR|1|ORD-7890||85025^CBC WITH DIFF^LN|||20240315120000|||||||||PROVIDER^JANE^MD\r";

    // ─── Two ORC/OBR pairs ─────────────────────────────────────────────────
    // Deviation: adds a second ORC + OBR for a BMP order.
    // Purpose: validates Bug 4 fix — legacy parser overwrote OrderId on every ORC/OBR;
    //          rewritten parser must preserve both groups in OrderGroups[0] and [1].
    public static readonly string TwoOrderGroupsMessage =
        "MSH|^~\\&|OrderSystem|HospitalA|LabSystem|HospitalB|20240315120000||ORM^O01|MSG001|P|2.4|||AL|NE\r" +
        "PID|1||123456^^^HospitalA^MR||DOE^JOHN^A||19800101|M\r" +
        "ORC|NW|ORD-7890|||||^^^20240315||20240315120000|||PROVIDER^JANE^MD\r" +
        "OBR|1|ORD-7890||85025^CBC WITH DIFF^LN|||20240315120000|||||||||PROVIDER^JANE^MD\r" +
        "ORC|NW|ORD-7891|||||^^^20240315||20240315120000|||PROVIDER^JANE^MD\r" +
        "OBR|2|ORD-7891||80048^BMP^LN|||20240315120000|||||||||PROVIDER^JANE^MD\r";

    // ─── Missing MSH-9 ─────────────────────────────────────────────────────
    // Deviation: MSH-9 (field index 8) is empty — "||" instead of "|ORM^O01|".
    // Purpose: validates graceful handling of missing MessageType.
    //          MSH-9 is conditionally required. The parser must not throw — it must
    //          return a record with MessageType = null, allowing ACK generation to proceed.
    public static readonly string MissingMessageTypeMessage =
        "MSH|^~\\&|OrderSystem|HospitalA|LabSystem|HospitalB|20240315120000|||MSG001|P|2.4|||AL|NE\r" +
        "PID|1||123456^^^HospitalA^MR||DOE^JOHN^A||19800101|M\r" +
        "ORC|NW|ORD-7890|||||^^^20240315||20240315120000|||PROVIDER^JANE^MD\r" +
        "OBR|1|ORD-7890||85025^CBC WITH DIFF^LN|||20240315120000|||||||||PROVIDER^JANE^MD\r";

    // ─── No OBR segment ────────────────────────────────────────────────────
    // Deviation: OBR segment is absent. ORC-only messages are valid (e.g., order cancel,
    //            status change, or hold where no service detail is required).
    // Purpose: validates that OrderGroup is populated from ORC with null OBR fields,
    //          not dropped. HasObrSegment = false.
    public static readonly string OrcOnlyMessage =
        "MSH|^~\\&|OrderSystem|HospitalA|LabSystem|HospitalB|20240315120000||ORM^O01|MSG001|P|2.4|||AL|NE\r" +
        "PID|1||123456^^^HospitalA^MR||DOE^JOHN^A||19800101|M\r" +
        "ORC|CA|ORD-7890|||CM||||20240315120000|||PROVIDER^JANE^MD\r";

    // ─── PID-3 with repeating identifiers (MR preferred) ──────────────────
    // Deviation: PID-3 has two repetitions separated by ~. MR is the second repetition.
    // Purpose: validates Bug 2 fix — legacy parser did Split('^')[0] on the full PID-3
    //          string, which accidentally worked only when MR was first. Here MR is second.
    //          The new parser must split on ~ first, then select by type code.
    public static readonly string Pid3RepeatingWithMrSecondMessage =
        "MSH|^~\\&|OrderSystem|HospitalA|LabSystem|HospitalB|20240315120000||ORM^O01|MSG001|P|2.4|||AL|NE\r" +
        "PID|1||98765^^^LAB^PI~123456^^^HospitalA^MR||DOE^JOHN^A||19800101|M\r" +
        "ORC|NW|ORD-7890|||||^^^20240315||20240315120000|||PROVIDER^JANE^MD\r" +
        "OBR|1|ORD-7890||85025^CBC WITH DIFF^LN|||20240315120000|||||||||PROVIDER^JANE^MD\r";

    // ─── PID-3 plain ID with no ^ components ───────────────────────────────
    // Deviation: PID-3 is a bare ID with no component separators — "789012" only.
    // Purpose: validates that a plain ID (no assigning authority, no type code) is
    //          returned as-is with PatientIdType = null.
    public static readonly string Pid3PlainIdMessage =
        "MSH|^~\\&|OrderSystem|HospitalA|LabSystem|HospitalB|20240315120000||ORM^O01|MSG002|P|2.4|||AL|NE\r" +
        "PID|1||789012||SMITH^JANE||19750601|F\r" +
        "ORC|NW|ORD-5555|||||^^^20240315||20240315120000|||PROVIDER^JANE^MD\r" +
        "OBR|1|ORD-5555||85025^CBC WITH DIFF^LN|||20240315120000|||||||||PROVIDER^JANE^MD\r";

    // ─── OBR-6 populated but ORC-9 and OBR-7 absent ───────────────────────
    // Deviation: OBR-6 (deprecated Requested Date/Time) is populated ("20240101000000"),
    //            but ORC-9 and OBR-7 are both empty.
    // Purpose: validates that OBR-6 is NEVER read. OrderedDateTime must be null.
    //          This is the direct regression test for Bug 3.
    public static readonly string Obr6PopulatedOrcAndObr7EmptyMessage =
        "MSH|^~\\&|OrderSystem|HospitalA|LabSystem|HospitalB|20240315120000||ORM^O01|MSG003|P|2.4|||AL|NE\r" +
        "PID|1||123456^^^HospitalA^MR||DOE^JOHN^A||19800101|M\r" +
        "ORC|NW|ORD-7890|||||^^^20240315||||PROVIDER^JANE^MD\r" +   // ORC-9 empty (field 9 = empty)
        "OBR|1|ORD-7890||85025^CBC WITH DIFF^LN||20240101000000|||||||||||PROVIDER^JANE^MD\r";
    //                                                              ↑ OBR-6 populated
    //                                                                        ↑ OBR-7 empty

    // ─── HL7 escape sequence in patient name ───────────────────────────────
    // Deviation: patient last name contains \S\ (the HL7 escape for the component separator ^).
    // Purpose: validates Bug 5 fix — the legacy parser stored escape sequences literally
    //          ("O\S\Brien"). NHapi automatically decodes escape sequences on field access,
    //          so the stored name should be "O^Brien" (a literal caret), not "O\S\Brien".
    //
    // Edge case choice justification: this is the highest-impact NHapi-vs-legacy behavioural
    // difference for a non-ASCII HL7 feature. A patient named O'Brien (often represented as
    // O\S\Brien in older HL7 systems) would be permanently corrupted in the database if the
    // escape sequence survives into storage. This test guards against that regression
    // regardless of future parser changes.
    public static readonly string EscapeSequenceInNameMessage =
        "MSH|^~\\&|OrderSystem|HospitalA|LabSystem|HospitalB|20240315120000||ORM^O01|MSG004|P|2.4|||AL|NE\r" +
        "PID|1||123456^^^HospitalA^MR||O\\S\\BRIEN^PATRICK||19800101|M\r" +
        "ORC|NW|ORD-7890|||||^^^20240315||20240315120000|||PROVIDER^JANE^MD\r" +
        "OBR|1|ORD-7890||85025^CBC WITH DIFF^LN|||20240315120000|||||||||PROVIDER^JANE^MD\r";

    // ─── \n line endings (normalization test) ─────────────────────────────
    // Deviation: uses \n instead of \r as the segment terminator.
    // Purpose: validates NormalizeLineEndings. The fixture produces identical parsed
    //          output as HappyPathMessage — this is asserted in the normalization test.
    public static readonly string HappyPathMessageLfLineEndings =
        "MSH|^~\\&|OrderSystem|HospitalA|LabSystem|HospitalB|20240315120000||ORM^O01|MSG001|P|2.4|||AL|NE\n" +
        "PID|1||123456^^^HospitalA^MR||DOE^JOHN^A||19800101|M\n" +
        "ORC|NW|ORD-7890|||||^^^20240315||20240315120000|||PROVIDER^JANE^MD\n" +
        "OBR|1|ORD-7890||85025^CBC WITH DIFF^LN|||20240315120000|||||||||PROVIDER^JANE^MD\n";
}
