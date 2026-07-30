using System.Text.Json;
using GigaClaw.Core.Automation.Policy;
using GigaClaw.Core.Tests.Helpers;

namespace GigaClaw.Core.Tests.Automation;

/// <summary>
/// R3's git guards. The agent under test is `committer`, which holds the git-write capability —
/// the point is that holding it is not a licence to rewrite history or skip the repo's own gates.
/// </summary>
public class PolicyGitGuardTests
{
    [Theory]
    // Gate-skipping: the move a policy layer exists to notice.
    [InlineData("git commit --no-verify -m 'wip'")]
    [InlineData("git commit -n -m 'wip'")]
    [InlineData("git push --no-verify")]
    // History rewriting and data loss.
    [InlineData("git push --force origin main")]
    [InlineData("git push -f origin main")]
    [InlineData("git push --delete origin feature")]
    [InlineData("git reset --hard HEAD~3")]
    [InlineData("git clean -fdx")]
    [InlineData("git checkout --force main")]
    [InlineData("git branch -D lane/cx-runtime")]
    [InlineData("git tag -d v1.0.0")]
    [InlineData("git filter-branch --tree-filter 'rm -f secret' HEAD")]
    [InlineData("git reflog expire --expire=now --all")]
    // Compound commands: the destructive half must still be seen.
    [InlineData("dotnet test && git push --force")]
    public async Task Destructive_and_gate_skipping_git_is_a_violation(string command)
    {
        var decisions = await EvaluateBashAsync(command);

        Assert.Contains(
            decisions,
            d => d.Call.Operation == PolicyToolOperation.GitDestructive && d.Decision.IsViolation);
    }

    [Theory]
    [InlineData("git commit -m 'ordinary work'")]
    [InlineData("git push origin main")]
    [InlineData("git push --force-with-lease origin main")]
    [InlineData("git add src/app.cs")]
    [InlineData("git status")]
    [InlineData("git log --oneline -5")]
    // -n is --dry-run on push. Flagging git's safest command would train people to ignore the gate.
    [InlineData("git push -n origin main")]
    public async Task Ordinary_git_is_not_treated_as_destructive(string command)
    {
        var decisions = await EvaluateBashAsync(command);

        Assert.DoesNotContain(
            decisions,
            d => d.Call.Operation == PolicyToolOperation.GitDestructive);
    }

    [Fact]
    public async Task A_destructive_command_is_still_reported_as_an_ordinary_git_write()
    {
        // Collapsing the two would lose the capability row the SP-1 inventory is built from.
        var decisions = await EvaluateBashAsync("git push --force origin main");

        Assert.Contains(decisions, d => d.Call.Operation == PolicyToolOperation.GitDestructive);
        Assert.Contains(decisions, d => d.Call.Operation == PolicyToolOperation.GitWrite);
    }

    [Fact]
    public async Task The_git_write_capability_does_not_excuse_a_destructive_command()
    {
        var policy = await LoadCommitterAsync(PolicyEnforcementMode.Block);

        // The same agent's ordinary git write is allowed...
        Assert.False(policy.Evaluate(PolicyToolCall.GitWrite("git commit -m x")).IsViolation);
        // ...while the destructive form is not, and is enforced.
        var destructive = policy.Evaluate(PolicyToolCall.GitDestructive("git push --force"));
        Assert.True(destructive.IsViolation);
        Assert.True(policy.Enforces(destructive));
    }

    [Fact]
    public async Task An_agent_still_in_shadow_mode_records_the_attempt_without_stopping_it()
    {
        var policy = await LoadCommitterAsync(PolicyEnforcementMode.Warn);

        var destructive = policy.Evaluate(PolicyToolCall.GitDestructive("git reset --hard"));

        Assert.True(destructive.IsViolation);
        Assert.False(policy.Enforces(destructive));
    }

    [Fact]
    public async Task The_reason_names_the_command_so_the_receipt_is_actionable()
    {
        var policy = await LoadCommitterAsync(PolicyEnforcementMode.Block);

        var decision = policy.Evaluate(PolicyToolCall.GitDestructive("git clean -fdx"));

        Assert.Contains("git clean -fdx", decision.Reason, StringComparison.Ordinal);
        Assert.Contains("committer", decision.Reason, StringComparison.Ordinal);
    }

    private static async Task<IReadOnlyList<PolicyHookEvaluation>> EvaluateBashAsync(string command)
    {
        var policy = await LoadCommitterAsync(PolicyEnforcementMode.Block);
        using var document = JsonDocument.Parse(
            JsonSerializer.Serialize(new { command }));
        return PolicyHookToolCallAdapter.Evaluate(policy, "Bash", document.RootElement);
    }

    private static async Task<ContractPolicy> LoadCommitterAsync(PolicyEnforcementMode mode)
    {
        using var tmp = new TempDir();
        var manifest = Path.Combine(tmp.Path, "contracts.json");
        await File.WriteAllTextAsync(manifest, $$"""
            {
              "version": 1,
              "defaults": {
                "maxDispatchAttempts": 3,
                "retryBackoffSeconds": 300,
                "requireAtomicHandoff": true,
                "requireAuthorOnBoardWrites": true
              },
              "agents": {
                "committer": {
                  "enforcement": "{{(mode == PolicyEnforcementMode.Block ? "block" : "warn")}}",
                  "dispatches": ["assignment"],
                  "riskClass": "git-write",
                  "allowedWriteGlobs": ["**"],
                  "ticketExit": ["Done"]
                }
              }
            }
            """);

        var policy = await ContractPolicyLoader.LoadManifestAsync(
            manifest,
            tmp.Path,
            "committer",
            caseSensitivity: PathCaseSensitivity.Sensitive);
        Assert.True(policy.IsValid, policy.Diagnostic);
        return policy;
    }
}
