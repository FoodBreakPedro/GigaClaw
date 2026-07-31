using System.Text;
using GigaClaw.Core.Tests.Helpers;
using Xunit;

namespace GigaClaw.Core.Tests.Packs;

/// <summary>
/// The shipped validators must survive a legacy Windows console.
///
/// <para>Python on Windows defaults <c>sys.stdout</c> to the ANSI code page — cp1252 on a Western
/// install — so a script that prints U+2192 raises <c>UnicodeEncodeError</c> <em>after</em> the
/// validation it was asked to do has already succeeded. The exit code becomes 2 and a valid
/// handoff reads as rejected. These scripts ship into every workspace, so that is a product bug,
/// and it was invisible to CI on Linux and macOS where the default is already UTF-8.</para>
///
/// <para>Pinning <c>PYTHONIOENCODING</c> reproduces it anywhere, which is the point: the defect is
/// Windows-only but the test for it is not. Both halves are asserted — that the script exits clean,
/// and that the characters actually survive the round trip rather than being silently degraded.</para>
/// </summary>
public class TemplateScriptEncodingTests
{
    /// <summary>The narrowest single-byte encoding a Windows console realistically reports.</summary>
    private const string WindowsConsoleEncoding = "cp1252";

    public static TheoryData<string, string[]> NonAsciiPrintingScripts() => new()
    {
        { "handoff_contract.py", ["--self-test"] },
        { "verdict_contract.py", ["--self-test"] },
        { "cognitive_load.py", [Sample] },
        { "lint_prose.py", [Sample] },
    };

    private static string Sample =>
        Path.Combine(PythonContractRunner.RepositoryRoot, "README.md");

    [Theory]
    [MemberData(nameof(NonAsciiPrintingScripts))]
    public void Shipped_validators_do_not_crash_on_a_cp1252_console(string script, string[] arguments)
    {
        var (exitCode, output) = PythonContractRunner.RunScriptWithIoEncoding(
            script, WindowsConsoleEncoding, arguments);

        Assert.False(
            output.Contains("UnicodeEncodeError", StringComparison.Ordinal)
            || output.Contains("codec can't encode", StringComparison.Ordinal),
            $"{script} still writes to the console under the process default encoding:" +
            $"{Environment.NewLine}{output}");

        // Exit 2 is how these scripts report an unhandled exception; 0/1 are verdicts.
        Assert.True(exitCode is 0 or 1, $"{script} exited {exitCode}:{Environment.NewLine}{output}");
    }

    [Fact]
    public void The_handoff_validator_still_prints_its_arrow_under_cp1252()
    {
        var fixture = Directory
            .GetFiles(Path.Combine(PythonContractRunner.FixturesDir("handoffs"), "valid"), "*.json")
            .Order(StringComparer.Ordinal)
            .First();

        var (exitCode, output) = PythonContractRunner.RunScriptWithIoEncoding(
            "handoff_contract.py", WindowsConsoleEncoding, fixture);

        Assert.True(exitCode == 0, $"{Path.GetFileName(fixture)} should be valid:{Environment.NewLine}{output}");
        // The fix pins the stream to UTF-8 rather than degrading the output to ASCII, so the
        // characters that caused the crash must still be there.
        Assert.Contains("→", output, StringComparison.Ordinal);
        Assert.Contains("·", output, StringComparison.Ordinal);
    }

    /// <summary>
    /// Guards the fix at its source. A new non-ASCII <c>print</c> in a script that has not pinned
    /// its streams reintroduces exactly this bug, and would again only show up on Windows.
    /// </summary>
    [Fact]
    public void Every_shipped_script_that_prints_non_ascii_pins_its_streams()
    {
        var offenders = new List<string>();

        foreach (var path in Directory.GetFiles(PythonContractRunner.ScriptsDir, "*.py").Order(StringComparer.Ordinal))
        {
            var text = File.ReadAllText(path, Encoding.UTF8);
            var printsNonAscii = text
                .Split('\n')
                .Where(line => line.TrimStart().StartsWith("print(", StringComparison.Ordinal))
                .Any(line => line.Any(c => c > 127));
            if (!printsNonAscii) continue;

            if (!text.Contains("reconfigure(encoding=\"utf-8\")", StringComparison.Ordinal))
                offenders.Add(Path.GetFileName(path));
        }

        Assert.True(
            offenders.Count == 0,
            "These shipped scripts print non-ASCII but never reconfigure sys.stdout, so they will " +
            "raise UnicodeEncodeError on a cp1252 Windows console after their work has already " +
            $"succeeded: {string.Join(", ", offenders)}");
    }
}
