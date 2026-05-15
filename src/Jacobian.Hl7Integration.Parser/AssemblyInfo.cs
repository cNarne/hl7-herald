// Grant test assembly access to internal members (NormalizeLineEndings, ParseDtm,
// ExtractPatientId, ExtractOrderGroups) so they can be tested directly without
// making them public. This is the standard pattern for white-box unit testing of
// internal helpers in C# — preferred over making them public, which would widen
// the published API surface.
[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("Jacobian.Hl7Integration.Parser.Tests")]
