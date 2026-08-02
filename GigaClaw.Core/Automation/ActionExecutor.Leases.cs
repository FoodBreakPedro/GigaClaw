using System.Collections.Concurrent;
using System.Net.Http;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using GigaClaw.Core.Automation.Triggers;
using GigaClaw.Core.Automation.Handoffs;
using GigaClaw.Core.Automation.Policy;
using GigaClaw.Core.Automation.Runners;
using GigaClaw.Core.Automation.Verdicts;
using GigaClaw.Core.Services;

namespace GigaClaw.Core.Automation;

internal sealed partial class ActionExecutor
{
    // ── R4: file-ownership leases ───────────────────────────────────────────

    /// <summary>
    /// The dispatch-time lease gate: resolves the ticket's declared file scope (handoff
    /// <c>ownedFiles</c>, falling back to the agent's own <c>allowedWriteGlobs</c> — see
    /// <see cref="FileLeaseScopeResolver"/>) and attempts to lease it for <paramref name="runId"/>.
    /// <see cref="FileLeaseGateOutcome.NotApplicable"/> covers every reason leasing does not apply
    /// to this dispatch: no store wired (pre-R4 behavior), no ticket, no declared scope, or a
    /// lease-store fault. That last case is a deliberate fail-<b>open</b> choice, unlike
    /// <see cref="ContractPolicy"/>'s fail-closed default for an unreadable manifest: a missing or
    /// malformed contracts.json is an authorization gap that must block every tool call, but a
    /// transient fault in this store (a locked file, a full disk) is an availability problem for a
    /// serialization aid — halting every dispatch in the project because the lease table hiccuped
    /// would be worse than the race it exists to prevent.
    /// <para>
    /// On a real conflict, block and warn mode diverge exactly the way R2/R3 diverge for every
    /// other policy violation: <see cref="FileLeaseGateOutcome.Blocked"/> (the agent's contract is
    /// in block mode) is real enforcement — the dispatch fails closed, the same as R3 denying an
    /// out-of-glob write. <see cref="FileLeaseGateOutcome.WarnedAndProceeded"/> (warn mode) mirrors
    /// R2's shadow mode: the conflict is recorded as a receipt, but the tool call — here, the
    /// dispatch itself — is not stopped. A warn-mode dispatch that proceeds through a conflict does
    /// not register a lease of its own (the scope it would have claimed is already held), so it is
    /// not tracked for serialization against a third run either; that is the acknowledged cost of
    /// shadow mode, and the same cost R2 accepts for an out-of-glob write. It is also the point of
    /// running R4 in warn mode at all: like R2's SP-1 inventory, it lets real conflicts happen and
    /// be recorded so an owner has evidence before flipping a given agent to block.
    /// </para>
    /// </summary>
    internal async Task<FileLeaseGateDecision> TryAcquireDispatchLeaseAsync(
        ProjectRuntime rt, int? ticketId, string agentName, string runId, CancellationToken ct)
    {
        if (_leases is null || ticketId is null || string.IsNullOrWhiteSpace(rt.Workspace))
            return FileLeaseGateDecision.NotApplicable;

        IReadOnlyList<string> scope;
        // Oldest-first, and reused twice: as the handoff source the leased scope is resolved from,
        // and as the receipt history the write-once denial check reads (see WriteFileLeaseReceiptAsync).
        var comments = new List<string>();
        try
        {
            Models.Ticket? ticket = null;
            try { ticket = await _tickets.GetTicketAsync(rt.Slug, ticketId.Value); }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "fileLease: could not read ticket #{Id} for scope resolution", ticketId);
            }

            if (ticket is not null)
                comments.AddRange(ticket.Comments.OrderBy(c => c.CreatedAt).Select(c => c.Content));
            scope = await FileLeaseScopeResolver.ResolveAsync(rt.Workspace!, comments, agentName, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "fileLease: scope resolution failed for agent {Agent} ticket #{Id} — dispatching unleased", agentName, ticketId);
            return FileLeaseGateDecision.NotApplicable;
        }

        if (scope.Count == 0)
            return FileLeaseGateDecision.NotApplicable;

        PolicyEnforcementMode enforcement;
        FileLeaseAcquireResult result;
        try
        {
            var policy = await ContractPolicyLoader.LoadAsync(rt.Workspace!, agentName, ct);
            enforcement = policy.Enforcement;
            result = await _leases.AcquireAsync(
                rt.Slug, ticketId.Value, runId, agentName, scope, DateTime.UtcNow, FileLeaseStore.DefaultTtl, ct: ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "fileLease: acquire failed for agent {Agent} ticket #{Id} — dispatching unleased", agentName, ticketId);
            return FileLeaseGateDecision.NotApplicable;
        }

        if (result.IsAcquired)
            return FileLeaseGateDecision.Granted;

        var outcome = enforcement == PolicyEnforcementMode.Block
            ? FileLeaseGateOutcome.Blocked
            : FileLeaseGateOutcome.WarnedAndProceeded;
        await WriteFileLeaseReceiptAsync(
            rt.Slug, ticketId.Value, agentName, scope, result.ConflictingLease!, enforcement, comments);
        _logger.LogWarning(
            "fileLease: {Outcome} agent={Agent} ticket=#{Ticket} run={Run} conflictsWith run={ConflictRun} agent={ConflictAgent}",
            outcome, agentName, ticketId, runId, result.ConflictingLease!.RunId, result.ConflictingLease.Agent);
        return new FileLeaseGateDecision(outcome);
    }

    /// <summary>Releases any active lease held by <paramref name="runId"/>. A no-op when no store is
    /// wired or the run never held one (e.g. its declared scope was empty).</summary>
    internal Task ReleaseDispatchLeaseAsync(string slug, string runId) =>
        _leases?.ReleaseAsync(slug, runId, DateTime.UtcNow) ?? Task.CompletedTask;

    /// <summary>
    /// Writes the queryable receipt for a file-lease conflict: a structured
    /// <c>file-lease-denial/v1</c> ticket comment naming the agent, its scope, and the conflicting
    /// lease — the same "denials/serializations produce receipts" idiom as R2's
    /// <c>policy-violation/v1</c> and R3's <c>outbound-denial/v1</c>
    /// (<see cref="WriteOutboundDenialReceiptAsync"/>).
    /// <para>
    /// <b>Once per conflict, not once per poll (SP-3 F2).</b> A blocked dispatch returns before
    /// <c>FinalizeAsync</c>, so its trigger firing is never committed and a repeating
    /// <c>ticketInColumn</c> trigger retries it every tick — deliberately, so the lane resumes the
    /// moment the lease frees, and that retry behavior is unchanged here. What changes is the noise:
    /// if the newest <c>file-lease-denial/v1</c> already on the ticket is byte-identical to the one
    /// this refusal would write, nothing is appended. The receipt is its own dedup key — same
    /// blocked agent, same scope, same conflicting lease means the same JSON, whereas a different
    /// lease, holder, ticket or scope produces different JSON and therefore a new receipt. That is
    /// the same first-refusal-only discipline R6 applies to <c>merge-held/v1</c>, and because the
    /// key is the durable comment rather than in-process memory it holds across a restart too.
    /// </para>
    /// </summary>
    private async Task WriteFileLeaseReceiptAsync(
        string slug,
        int ticketId,
        string agent,
        IReadOnlyList<string> scope,
        FileLease conflict,
        PolicyEnforcementMode enforcement,
        IReadOnlyList<string> priorCommentsOldestFirst)
    {
        var receipt = JsonSerializer.Serialize(new
        {
            schema = "file-lease-denial/v1",
            agent,
            action = "runAgent",
            scope,
            rule = "file-ownership-lease",
            conflictingLeaseId = conflict.LeaseId,
            conflictingRunId = conflict.RunId,
            conflictingAgent = conflict.Agent,
            conflictingTicketId = conflict.TicketId,
            reason = $"Scope overlaps an active lease held by '{conflict.Agent}' (run {conflict.RunId}) on ticket #{conflict.TicketId}.",
            enforcementMode = enforcement == PolicyEnforcementMode.Block ? "block" : "warn",
        });

        var newest = priorCommentsOldestFirst.LastOrDefault(
            c => c.Contains("file-lease-denial/v1", StringComparison.Ordinal));
        if (string.Equals(newest, receipt, StringComparison.Ordinal))
        {
            _logger.LogDebug(
                "fileLease: ticket #{Id} already carries this exact denial receipt — not appending another", ticketId);
            return;
        }

        try { await _tickets.AddCommentAsync(slug, ticketId, receipt, "automation"); }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "fileLease: failed to write file-lease-denial receipt for ticket #{Id}", ticketId);
        }
    }
}

/// <summary>Outcome of <see cref="ActionExecutor.TryAcquireDispatchLeaseAsync"/> (R4).</summary>
internal enum FileLeaseGateOutcome
{
    /// <summary>Leasing does not apply to this dispatch (no store wired, no ticket, no declared
    /// scope, or a lease-store fault) — dispatch proceeds exactly as it did pre-R4.</summary>
    NotApplicable,
    /// <summary>The lease was acquired; dispatch proceeds.</summary>
    Granted,
    /// <summary>A conflicting lease is active and the agent's contract is in warn mode: a
    /// <c>file-lease-denial/v1</c> receipt is written, but — mirroring R2's shadow mode, which
    /// records an out-of-glob write without stopping it — the dispatch is not skipped. It proceeds
    /// without holding a lease of its own.</summary>
    WarnedAndProceeded,
    /// <summary>A conflicting lease is active and the agent's contract is in block mode: this
    /// dispatch fails closed, the same way R3 denies an out-of-glob write.</summary>
    Blocked,
}

internal readonly record struct FileLeaseGateDecision(FileLeaseGateOutcome Outcome)
{
    public static readonly FileLeaseGateDecision NotApplicable = new(FileLeaseGateOutcome.NotApplicable);
    public static readonly FileLeaseGateDecision Granted = new(FileLeaseGateOutcome.Granted);
    public static readonly FileLeaseGateDecision WarnedAndProceeded = new(FileLeaseGateOutcome.WarnedAndProceeded);

    /// <summary>True only for <see cref="FileLeaseGateOutcome.Blocked"/> — block mode is real
    /// enforcement and fails closed; warn mode logs a receipt but does not stop the dispatch, the
    /// same warn/block split R2/R3 apply to every other policy violation.</summary>
    public bool ShouldSkip => Outcome == FileLeaseGateOutcome.Blocked;
}
