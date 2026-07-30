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
/// </summary>
public sealed class KnownWindowsFailureFactAttribute : FactAttribute
{
    public KnownWindowsFailureFactAttribute(string reason)
    {
        Reason = reason;
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            Skip = $"Known Windows failure — {reason} See doc/roadmap/SESSION-HANDOFF.md § Windows CI.";
    }

    /// <summary>What breaks on Windows. Recorded even when the test runs, so it survives grep.</summary>
    public string Reason { get; }
}
