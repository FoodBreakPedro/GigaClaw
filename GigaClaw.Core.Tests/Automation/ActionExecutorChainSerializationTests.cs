using System.Text.RegularExpressions;

namespace GigaClaw.Core.Tests.Automation;

/// <summary>
/// Tests for ticket #217: per-ticket action chain serialization and debounce-on-completion.
/// All tests are RED on dev (fields/signatures don't exist yet) and GREEN after implementation.
/// </summary>
public class ActionExecutorChainSerializationTests
{
    private static string ActionExecutorSrc =>
        File.ReadAllText(LocateRepoFile("GigaClaw.Core/Automation/ActionExecutor.cs"));

    private static string ITriggerSrc =>
        File.ReadAllText(LocateRepoFile("GigaClaw.Core/Automation/Triggers/ITrigger.cs"));

    private static string TicketInColumnTriggerSrc =>
        File.ReadAllText(LocateRepoFile("GigaClaw.Core/Automation/Triggers/TicketInColumnTrigger.cs"));

    private static string LocateRepoFile(string relative)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, relative.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }
        throw new FileNotFoundException($"Could not locate {relative} from {AppContext.BaseDirectory}");
    }

    // ── Case 1 + Case 4: in-flight tracking field ────────────────────────────

    [Fact]
    public void ActionExecutor_declares_inFlightChains_ConcurrentDictionary()
    {
        // RED: field doesn't exist on dev. GREEN: programmer adds _inFlightChains.
        var src = ActionExecutorSrc;
        Assert.Contains("_inFlightChains", src);
        Assert.Contains("ConcurrentDictionary", src);
    }

    // ── Case 1: second chain for same (automationId, ticketId) is blocked ────

    [Fact]
    public void ActionExecutor_ExecuteAutomationAsync_guards_dispatch_with_TryAdd()
    {
        // RED: TryAdd gate doesn't exist on dev; second chain would proceed.
        // GREEN: programmer adds the TryAdd check at the top of ExecuteAutomationAsync.
        var src = ActionExecutorSrc;
        Assert.Contains("TryAdd", src);

        // The guard must appear inside ExecuteAutomationAsync, not only in a helper.
        var execIdx = src.IndexOf("ExecuteAutomationAsync", StringComparison.Ordinal);
        Assert.True(execIdx >= 0, "ExecuteAutomationAsync not found");
        var execBody = src[execIdx..];
        var tryAddIdx = execBody.IndexOf("TryAdd", StringComparison.Ordinal);
        Assert.True(tryAddIdx >= 0, "TryAdd guard not found in ExecuteAutomationAsync body");
    }

    // ── Case 2: different ticket on same automation is NOT blocked ────────────

    [Fact]
    public void ChainKey_embeds_ticketId_so_different_tickets_are_independent()
    {
        // RED: no chainKey construction on dev.
        // GREEN: key uses both automation.Id and ticketId / tid so (automA, ticket 1) != (automA, ticket 2).
        var src = ActionExecutorSrc;
        Assert.True(
            Regex.IsMatch(src, @"chainKey|chain_key", RegexOptions.IgnoreCase),
            "Expected a 'chainKey' variable used to key in-flight chains");

        // The key must encode the ticketId so different tickets don't block each other.
        Assert.True(
            Regex.IsMatch(src, @"TicketId|ticketId|tid"),
            "chainKey must incorporate the ticketId to keep different-ticket chains independent");
    }

    // ── Case 3: slot released on failure (all exit paths) ────────────────────

    [Fact]
    public void HandleRunCompletionAsync_releases_slot_in_finally()
    {
        // RED: no TryRemove in HandleRunCompletionAsync on dev.
        // GREEN: programmer wraps the body in try/finally that calls _inFlightChains.TryRemove.
        var src = ActionExecutorSrc;
        var handleIdx = src.IndexOf("HandleRunCompletionAsync", StringComparison.Ordinal);
        Assert.True(handleIdx >= 0, "HandleRunCompletionAsync not found");
        var handleBody = src[handleIdx..];

        Assert.Contains("TryRemove", handleBody);
        Assert.Contains("finally", handleBody);
    }

    [Fact]
    public void ExecuteAutomationAsync_releases_slot_for_non_runAgent_path()
    {
        // RED: no release at the bottom of ExecuteAutomationAsync on dev.
        // GREEN: programmer adds try/finally so non-runAgent chains also release on completion or exception.
        var src = ActionExecutorSrc;
        var execIdx = src.IndexOf("ExecuteAutomationAsync", StringComparison.Ordinal);
        Assert.True(execIdx >= 0);
        var execBody = src[execIdx..];

        // TryRemove must appear in ExecuteAutomationAsync's own body (not only in HandleRunCompletionAsync).
        Assert.Contains("TryRemove", execBody);
    }

    // ── Case 4: executePowerShell (non-runAgent) chain also serialized ────────

    [Fact]
    public void InFlightChains_check_occurs_before_any_action_dispatch_not_only_runAgent()
    {
        // RED: dev has no _inFlightChains guard; executePowerShell chains are not serialized.
        // GREEN: the TryAdd guard fires at the top of ExecuteAutomationAsync before the action loop.
        var src = ActionExecutorSrc;

        // The TryAdd must come before the action switch — i.e., before "case RunAgentActionSpec"
        var execIdx = src.IndexOf("ExecuteAutomationAsync", StringComparison.Ordinal);
        Assert.True(execIdx >= 0);
        var execBody = src[execIdx..];

        var tryAddPos = execBody.IndexOf("TryAdd", StringComparison.Ordinal);
        var runAgentCasePos = execBody.IndexOf("RunAgentActionSpec", StringComparison.Ordinal);

        Assert.True(tryAddPos >= 0, "TryAdd not found in ExecuteAutomationAsync");
        Assert.True(runAgentCasePos >= 0, "RunAgentActionSpec switch case not found");
        Assert.True(tryAddPos < runAgentCasePos,
            "TryAdd guard must appear before the RunAgentActionSpec case so non-runAgent chains are also serialized");
    }

    // ── Case 4b: httpRequest chain also serialized ───────────────────────────

    [Fact]
    public void HttpRequest_detaches_the_rest_of_the_chain_like_the_other_blocking_actions()
    {
        // httpRequest blocks on network I/O, so it must join the detach condition alongside
        // consolidateAgentMemory / executePowerShell — otherwise one slow webhook freezes
        // trigger evaluation for every project on the engine tick.
        var src = ActionExecutorSrc;

        var detachIdx = src.IndexOf("background && action is", StringComparison.Ordinal);
        Assert.True(detachIdx >= 0, "Chain-detach condition not found in ActionExecutor");
        var condition = src[detachIdx..(detachIdx + 260)];

        Assert.Contains("HttpRequestActionSpec", condition);
        Assert.Contains("ConsolidateAgentMemoryActionSpec", condition);
        Assert.Contains("ExecutePowerShellActionSpec", condition);
    }

    [Fact]
    public void Detached_httpRequest_chain_is_guarded_by_the_inFlight_map()
    {
        // The detach path shares the _inFlightChains guard, so two overlapping firings for the
        // same (automation, ticket) can't both fire the webhook.
        var src = ActionExecutorSrc;
        var detachIdx = src.IndexOf("background && action is", StringComparison.Ordinal);
        Assert.True(detachIdx >= 0);
        var detachBody = src[detachIdx..];

        var guardIdx = detachBody.IndexOf("guardKey", StringComparison.Ordinal);
        Assert.True(guardIdx >= 0, "Detached chain must take an _inFlightChains guard slot");
        Assert.Contains("_inFlightChains", detachBody[..(guardIdx + 400)]);
    }

    // ── Post-run switch must not silently drop registered action types ────────

    [Fact]
    public void ProcessPostRunActionsAsync_handles_createTicket_and_httpRequest()
    {
        // createTicket had no arm here, so a createTicket following a runAgent was a silent no-op.
        var src = ActionExecutorSrc;
        var idx = src.IndexOf("ProcessPostRunActionsAsync", StringComparison.Ordinal);
        Assert.True(idx >= 0, "ProcessPostRunActionsAsync not found");
        var body = src[idx..];

        Assert.Contains("case CreateTicketActionSpec", body);
        Assert.Contains("case HttpRequestActionSpec", body);
    }

    [Fact]
    public void ProcessPostRunActionsAsync_has_a_default_arm_so_gaps_are_not_silent()
    {
        var src = ActionExecutorSrc;
        var idx = src.IndexOf("private async Task<bool> ProcessPostRunActionsAsync", StringComparison.Ordinal);
        Assert.True(idx >= 0, "ProcessPostRunActionsAsync declaration not found");
        var body = src[idx..];

        var defaultIdx = body.IndexOf("default:", StringComparison.Ordinal);
        Assert.True(defaultIdx >= 0, "Post-run switch must have a default arm");
        Assert.Contains("LogWarning", body[defaultIdx..(defaultIdx + 400)]);
    }

    // ── Case 5: debounce stamped at chain completion, not at emission ─────────

    [Fact]
    public void ITrigger_CommitFiringAsync_signature_accepts_completedAt()
    {
        // RED: current signature is CommitFiringAsync(TriggerContext, TriggerFiring) — no completedAt.
        // GREEN: programmer adds DateTime? completedAt = null to the default interface method.
        var src = ITriggerSrc;
        Assert.Contains("completedAt", src);
        Assert.Contains("DateTime?", src);

        // The parameter must appear on CommitFiringAsync specifically.
        var commitIdx = src.IndexOf("CommitFiringAsync", StringComparison.Ordinal);
        Assert.True(commitIdx >= 0);
        var commitSignature = src[commitIdx..(commitIdx + 200)];
        Assert.Contains("completedAt", commitSignature);
    }

    [Fact]
    public void TicketInColumnTrigger_CommitFiringAsync_uses_completedAt_for_debounce_stamp()
    {
        // RED: dev uses ctx.Now unconditionally — completedAt is not threaded through.
        // GREEN: programmer replaces ctx.Now with completedAt ?? ctx.Now in CommitFiringAsync.
        var src = TicketInColumnTriggerSrc;
        Assert.Contains("completedAt", src);
        Assert.True(
            Regex.IsMatch(src, @"completedAt\s*\?\?"),
            "CommitFiringAsync must write (completedAt ?? ctx.Now) as the debounce timestamp");
    }

    // ── Edge case: null ticketId skips serialization ──────────────────────────

    [Fact]
    public void ActionExecutor_skips_chain_key_when_ticketId_is_null()
    {
        // RED: no chainKey variable on dev — the assertion on "chainKey" itself fails.
        // GREEN: programmer introduces chainKey = null when firing.TicketId is null.
        var src = ActionExecutorSrc;
        Assert.Contains("chainKey", src);
        Assert.True(
            Regex.IsMatch(src, @"chainKey\s*=\s*null"),
            "chainKey must be explicitly set to null for the no-ticketId path so global automations skip serialization");
    }

    // ── Edge case: key includes automation id to prevent cross-automation collisions ──

    [Fact]
    public void ChainKey_includes_automation_id_to_prevent_cross_automation_collisions()
    {
        // RED: no chainKey on dev — first assertion fails.
        // GREEN: key embeds automation.Id so two distinct automations can each fire on the same ticket
        //        without blocking each other.
        var src = ActionExecutorSrc;
        Assert.Contains("chainKey", src);
        // The chain key string literal must include automation.Id (not just a log call).
        Assert.True(
            Regex.IsMatch(src, @"chainKey\s*=.*automation\.Id|automation\.Id.*chainKey"),
            "chainKey must embed automation.Id to avoid collisions between different automations");
    }
}
