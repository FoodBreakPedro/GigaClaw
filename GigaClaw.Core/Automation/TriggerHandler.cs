using Microsoft.Extensions.Logging;
using GigaClaw.Core.Automation.Triggers;
using GigaClaw.Core.Services;

namespace GigaClaw.Core.Automation;

/// <summary>
/// Evaluates triggers each engine tick: drains urgent signal firings first,
/// then polls each project's automations for scheduled/condition-based firings.
/// </summary>
internal sealed class TriggerHandler
{
    private readonly ProjectService _projects;
    private readonly ProjectRuntimeManager _runtimeManager;
    private readonly ActionExecutor _executor;
    private readonly TicketService _tickets;
    private readonly MemberService _members;
    private readonly SessionRegistry _sessions;
    private readonly AgentRunRegistry _runs;
    private readonly TeamRunService _teamRuns;
    private readonly Workflow.WorkflowWalker _walker;
    private readonly ILogger _logger;

    public TriggerHandler(
        ProjectService projects,
        ProjectRuntimeManager runtimeManager,
        ActionExecutor executor,
        TicketService tickets,
        MemberService members,
        SessionRegistry sessions,
        AgentRunRegistry runs,
        TeamRunService teamRuns,
        Workflow.WorkflowWalker walker,
        ILogger logger)
    {
        _walker = walker;
        _projects = projects;
        _runtimeManager = runtimeManager;
        _executor = executor;
        _tickets = tickets;
        _members = members;
        _sessions = sessions;
        _runs = runs;
        _teamRuns = teamRuns;
        _logger = logger;
    }

    public async Task ProcessTickAsync(CancellationToken ct)
    {
        // Drain urgent firings first (produced by NotifySignalAsync) before the regular poll.
        while (_runtimeManager.UrgentReader.TryRead(out var entry))
        {
            if (ct.IsCancellationRequested) return;
            var urgentProject = await _projects.GetProjectAsync(entry.Slug);
            if (urgentProject?.IsPaused == true) continue;
            await _runtimeManager.EnsureLoadedAsync(entry.Slug);
            if (!_runtimeManager.TryGetRuntime(entry.Slug, out var urt) || urt?.Config is null) continue;
            if (!await _executor.ConditionsMatchAsync(urt, entry.Automation, entry.Firing)) continue;
            var utctx = BuildTriggerContext(entry.Slug, urt.Workspace!, entry.Automation);
            // Mark the event consumed before dispatch so the poll path doesn't re-fire it.
            // On failure, dispatch anyway: a ~PollSeconds-late duplicate beats a lost firing.
            try { await entry.Trigger.ConsumeSignalFiringAsync(utctx, entry.Firing); }
            catch (Exception ex) { _logger.LogWarning(ex, "ConsumeSignalFiring failed for {Id} — poll may re-fire this event", entry.Automation.Id); }
            await _executor.ExecuteAutomationAsync(urt, entry.Automation, entry.Firing, ct, entry.Trigger, utctx);
        }

        var projects = await _projects.ListProjectsAsync();
        foreach (var project in projects)
        {
            if (ct.IsCancellationRequested) return;
            if (project.IsPaused) continue;
            await _runtimeManager.EnsureLoadedAsync(project.Slug);
            var rt = _runtimeManager.GetRuntime(project.Slug);
            if (rt.ConfigDirty)
            {
                // Disk changed outside the PUT /automations API (e.g. an agent editing automations.json
                // directly) — reload now so newly added/edited automations aren't silently unregistered
                // until someone happens to hit the UI's "Reload" button. See ticket lain#181/gigaclaw-front#116:
                // automations added this way sat dormant in rt.Triggers for weeks with no error or indication.
                _logger.LogInformation("Config change detected on disk for {Slug} — reloading", project.Slug);
                await _runtimeManager.ReloadProjectAsync(project.Slug);
            }
            // Team runs are reconciled from the board before the triggers are polled, so a task
            // released this tick is already in the dispatch column when its agent's trigger looks.
            // This is also the whole resume path: after a restart there is no in-memory run state
            // to rebuild — the first tick reads the open runs and continues from the tickets.
            await _teamRuns.ReconcileProjectAsync(project.Slug);
            // The workflow walk is reconciled on the same terms and for the same reasons, right
            // after the team runs it delegates its fan-outs to: a state entered this tick already
            // has its sub-ticket in the dispatch column when the agent's trigger looks, and the
            // walk resumes after a restart from the receipts on the ticket with nothing rebuilt.
            await _walker.ReconcileProjectAsync(rt, ct);

            if (rt.Config is null) continue;
            foreach (var automation in rt.Config.Automations)
            {
                if (!automation.Enabled) continue;
                if (!rt.Triggers.TryGetValue(automation.Id, out var trigger)) continue;
                var tctx = BuildTriggerContext(project.Slug, rt.Workspace!, automation);
                IReadOnlyList<TriggerFiring> firings;
                try { firings = await trigger.EvaluateAsync(tctx, ct); }
                catch (Exception ex) { _logger.LogWarning(ex, "Trigger eval failed for {Id}", automation.Id); continue; }
                foreach (var firing in firings)
                {
                    if (!await _executor.ConditionsMatchAsync(rt, automation, firing)) continue;
                    // Awaited: the prep phase runs to completion before the next firing, reserving
                    // concurrency slots. The actual subprocess is fire-and-forget inside ExecuteRunAgentActionAsync.
                    await _executor.ExecuteAutomationAsync(rt, automation, firing, ct, trigger, tctx);
                }
            }
        }
    }

    private TriggerContext BuildTriggerContext(string slug, string workspace, Automation automation) =>
        new()
        {
            ProjectSlug = slug,
            WorkspacePath = workspace,
            Automation = automation,
            Tickets = _tickets,
            Members = _members,
            Sessions = _sessions,
            Runs = _runs,
            Now = DateTime.UtcNow,
        };
}
