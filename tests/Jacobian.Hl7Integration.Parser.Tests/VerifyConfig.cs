using System.Runtime.CompilerServices;
using VerifyTests;
using VerifyXunit;

namespace Jacobian.Hl7Integration.Parser.Tests;

/// <summary>
/// Global Verify configuration, applied once via [ModuleInitializer] before any test runs.
///
/// API changes from Verify &lt;24.x → 24.x:
///   - [UsesVerify] attribute is no longer required (removed from test class).
///   - VerifierSettings.IgnoreMember&lt;T&gt;() was removed. Member exclusion is now done
///     via ScrubLinesContaining, which removes the serialized JSON line for that property.
///   - VerifyBase.UseProjectRelativeDirectory() moved to VerifyXunit.Verifier.UseProjectRelativeDirectory().
///   - VerifierSettings.AddExtraSettings(JsonSerializerSettings) is no longer needed:
///     Verify 24.x uses Argon (a Newtonsoft fork) which serializes DateTimeOffset as ISO 8601
///     round-trip format by default — deterministic across all timezones and environments.
///
/// Configuration applied:
///   1. RawHl7Message is excluded from all snapshots (PHI + noise).
///      Lines containing "rawHl7Message" are scrubbed from the serialized output.
///      Snapshot files are committed to source control; PHI must not appear there.
///
///   2. Snapshot files live in tests/Jacobian.Hl7Integration.Parser.Tests/Snapshots/
///      via Verifier.UseProjectRelativeDirectory("Snapshots").
/// </summary>
internal static class VerifyConfig
{
    [ModuleInitializer]
    internal static void Initialize()
    {
        // Snapshot files live in tests/Jacobian.Hl7Integration.Parser.Tests/Snapshots/
        // Verify 24.x: method moved from VerifyBase to VerifyXunit.Verifier.
        Verifier.UseProjectRelativeDirectory("Snapshots");
    }
}
