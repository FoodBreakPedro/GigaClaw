using System.Runtime.InteropServices;
using Xunit;

namespace GigaClaw.Eval.Tests.Helpers;

/// <summary>
/// A test that passes on Linux and macOS and is <em>known to fail on Windows</em> for a reason that
/// has been diagnosed but not yet fixed.
///
/// <para>This is a deliberate deferral, not a fix, and it is deliberately loud. The alternative —
/// leaving the Windows job red — is worse and this repository has the evidence: CI had been failing
/// on <c>windows-latest</c> since before 2026-07-30, and because red was the normal state, two real
/// defects sat in it unnoticed. A permanently failing job stops being read. A green job with three
/// named, dated exemptions still gets read.</para>
///
/// <para>Every use must carry a <see cref="Reason"/> that says what actually breaks and where the
/// investigation is written up. Do not use this to silence a failure you have not diagnosed.</para>
///
/// <para>Set <c>GIGACLAW_RUN_KNOWN_WINDOWS_FAILURES=1</c> to run them anyway. A skipped test emits
/// no diagnostics, so an exemption that can only be skipped is an exemption that can never be
/// investigated from CI — which is how the one remaining entry stayed undiagnosed across releases.
/// This is the one-command way to get a real failure message off a Windows machine without making
/// the default build red.</para>
/// </summary>
public sealed class KnownWindowsFailureFactAttribute : FactAttribute
{
    /// <summary>Opt back in, so the exemption can be interrogated instead of only silenced.</summary>
    public const string RunAnywayVariable = "GIGACLAW_RUN_KNOWN_WINDOWS_FAILURES";

    public KnownWindowsFailureFactAttribute(string reason)
    {
        Reason = reason;
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            && Environment.GetEnvironmentVariable(RunAnywayVariable) != "1")
        {
            Skip = $"Known Windows failure — {reason} See doc/roadmap/SESSION-HANDOFF.md § Windows CI. " +
                   $"Set {RunAnywayVariable}=1 to run it anyway and read the diagnostics.";
        }
    }

    /// <summary>What breaks on Windows. Recorded even when the test runs, so it survives grep.</summary>
    public string Reason { get; }
}
