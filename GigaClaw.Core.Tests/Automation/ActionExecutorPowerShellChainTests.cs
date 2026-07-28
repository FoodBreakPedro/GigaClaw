using System.Net;
using GigaClaw.Core.Automation;
using GigaClaw.Core.Automation.Triggers;
using GigaClaw.Core.Models;
using GigaClaw.Core.Services;
using GigaClaw.Core.Tests.Helpers;
using Microsoft.Extensions.Logging.Abstractions;
using AutomationRule = GigaClaw.Core.Automation.Automation;

namespace GigaClaw.Core.Tests.Automation;

/// <summary>
/// Covers the executePowerShell action's chain-value capture (Task 16): stdout/exit code are
/// published into <c>ActionState.ChainValues</c> as <c>{powershell.stdout}</c> /
/// <c>{powershell.exitCode}</c>, the same "publish first" shape <see cref="HttpRequestActionSpec"/>
/// uses for <c>{http.status}</c>/<c>{http.body}</c> — this is what lets the
/// <c>cms-dispatch-on-done</c> archive step's <c>archive-draft.ps1</c> report its outcome back
/// onto the ticket via a follow-up <c>addComment</c>.
/// <para>
/// Real-process tests are skipped (not failed) when no PowerShell interpreter is on PATH — the
/// same graceful-degradation posture <see cref="ShellResolver"/> exists for. CI (windows-latest)
/// and any dev machine with pwsh installed exercise the real subprocess path; everywhere else
/// falls back to source-level assertions so the contract is still checked.
/// </para>
/// </summary>
public class ActionExecutorPowerShellChainTests
{
    private static bool PowerShellAvailable =>
        ShellResolver.TryFindOnPath("pwsh") || ShellResolver.TryFindOnPath("powershell");

    // ── Harness ─────────────────────────────────────────────────────────────

    private sealed class Harness : IDisposable
    {
        public required TempDir Tmp { get; init; }
        public required TicketService Tickets { get; init; }
        public required MemberService Members { get; init; }
        public required SessionRegistry Sessions { get; init; }
        public required AgentRunRegistry Runs { get; init; }
        public required ActionExecutor Executor { get; init; }
        public required ProjectRuntime Runtime { get; init; }
        public required int TicketId { get; init; }
        public required string Slug { get; init; }

        public void Dispose() => Tmp.Dispose();
    }

    private static async Task<Harness> BuildAsync(IHttpClientFactory? httpClientFactory = null)
    {
        var tmp = new TempDir();
        var projects = new ProjectService(tmp.Path);
        var project = await projects.CreateProjectAsync("powershell-chain-test");
        var workspace = projects.ResolveWorkspacePath(project);
        Directory.CreateDirectory(workspace);

        var members = new MemberService(projects);
        var tickets = new TicketService(projects, members);
        var labels = new LabelService(projects);
        var sessions = new SessionRegistry();
        var runs = new AgentRunRegistry();
        var cost = new CostTracker();
        var runner = new ClaudeRunner(sessions, runs, new RunConcurrencyGate(1), NullLogger<ClaudeRunner>.Instance);
        var loc = new LocalizationService(new AppSettingsService(tmp.Path));

        var executor = new ActionExecutor(
            tickets, members, labels, sessions, runs, runner, cost, loc, projects,
            new RunStateManager(runs, cost, tickets, NullLogger.Instance),
            httpClientFactory ?? FakeHttpClientFactory.Unused,
            NullLogger.Instance);

        var ticket = await tickets.CreateTicketAsync(project.Slug, "Publish the launch post", status: "Review");

        return new Harness
        {
            Tmp = tmp,
            Tickets = tickets,
            Members = members,
            Sessions = sessions,
            Runs = runs,
            Executor = executor,
            TicketId = ticket.Id,
            Slug = project.Slug,
            Runtime = new ProjectRuntime(project.Slug)
            {
                Workspace = workspace,
                Config = new AutomationConfig { Automations = [] },
            },
        };
    }

    /// <summary>Captures the chain's terminal finalize so success paths need no polling.</summary>
    private sealed class CompletionTrigger : ITrigger
    {
        public readonly TaskCompletionSource<bool> Completed =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<IReadOnlyList<TriggerFiring>> EvaluateAsync(TriggerContext ctx, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<TriggerFiring>>(Array.Empty<TriggerFiring>());

        public Task CompleteFiringAsync(TriggerContext ctx, TriggerFiring firing, bool succeeded, DateTime? completedAt = null)
        {
            Completed.TrySetResult(succeeded);
            return Task.CompletedTask;
        }
    }

    private static TriggerContext BuildContext(Harness h, AutomationRule automation) => new()
    {
        ProjectSlug = h.Slug,
        WorkspacePath = h.Runtime.Workspace!,
        Automation = automation,
        Tickets = h.Tickets,
        Members = h.Members,
        Sessions = h.Sessions,
        Runs = h.Runs,
        Now = DateTime.UtcNow,
    };

    private static async Task RunToCompletionAsync(Harness h, params ActionSpec[] actions)
    {
        var automation = new AutomationRule
        {
            Id = "ps-chain",
            Trigger = new StatusChangeTriggerSpec { To = "Review" },
            Actions = actions.ToList(),
        };
        var trigger = new CompletionTrigger();
        var firing = new TriggerFiring(h.TicketId, "Publish the launch post", "Review");

        await h.Executor.ExecuteAutomationAsync(
            h.Runtime, automation, firing, CancellationToken.None, trigger, BuildContext(h, automation));

        var finished = await Task.WhenAny(trigger.Completed.Task, Task.Delay(TimeSpan.FromSeconds(15)));
        Assert.True(finished == trigger.Completed.Task, "Action chain did not finalize within 15s");
    }

    private static async Task<List<string>> CommentsAsync(Harness h)
    {
        var ticket = await h.Tickets.GetTicketAsync(h.Slug, h.TicketId);
        return ticket!.Comments.Select(c => c.Content).ToList();
    }

    // ── Real subprocess coverage (skipped without a PowerShell interpreter) ──

    [Fact]
    public async Task Stdout_is_published_to_the_chain_for_a_later_addComment()
    {
        if (!PowerShellAvailable) return; // no pwsh/powershell on PATH — see class remarks

        using var h = await BuildAsync();

        await RunToCompletionAsync(h,
            new ExecutePowerShellActionSpec
            {
                Script = "Write-Output 'hello from archive script'",
                TimeoutSeconds = 15,
                AbortOnFailure = false,
            },
            new AddCommentActionSpec { Content = "result: {powershell.stdout} exit={powershell.exitCode}", Author = "automation" });

        Assert.Equal("result: hello from archive script exit=0", Assert.Single(await CommentsAsync(h)));
    }

    [Fact]
    public async Task NonZero_exit_still_publishes_stdout_and_does_not_abort_when_AbortOnFailure_is_false()
    {
        if (!PowerShellAvailable) return;

        using var h = await BuildAsync();

        await RunToCompletionAsync(h,
            new ExecutePowerShellActionSpec
            {
                Script = "Write-Output 'partial output'; exit 3",
                TimeoutSeconds = 15,
                AbortOnFailure = false,
            },
            new AddCommentActionSpec { Content = "result: {powershell.stdout} exit={powershell.exitCode}", Author = "automation" });

        Assert.Equal("result: partial output exit=3", Assert.Single(await CommentsAsync(h)));
    }

    [Fact]
    public async Task Ticket_slug_and_id_placeholders_render_in_arguments()
    {
        if (!PowerShellAvailable) return;

        using var h = await BuildAsync();

        await RunToCompletionAsync(h,
            new ExecutePowerShellActionSpec
            {
                // $args[0]/[1] are the rendered {ticketId}/{slug} — mirrors how archive-draft.ps1
                // receives -TicketId/-ProjectSlug as positional arguments from automations.json.
                Script = "Write-Output \"ticket=$($args[0]) slug=$($args[1])\"",
                Arguments = ["{ticketId}", "{slug}"],
                TimeoutSeconds = 15,
                AbortOnFailure = false,
            },
            new AddCommentActionSpec { Content = "{powershell.stdout}", Author = "automation" });

        Assert.Equal($"ticket={h.TicketId} slug={h.Slug}", Assert.Single(await CommentsAsync(h)));
    }

    [Fact]
    public async Task Chain_values_from_an_earlier_httpRequest_reach_executePowerShell_arguments()
    {
        if (!PowerShellAvailable) return;

        // The real cms-dispatch-on-done shape: httpRequest (CMS dispatch) publishes
        // {http.body.adminUrl} into ActionState.ChainValues, and the archive step three actions
        // later must be able to read it as one of its script arguments — proving
        // executePowerShell's Render() draws from the same chain-values bag httpRequest writes
        // into, not a private one scoped to httpRequest alone.
        var handler = FakeHttpMessageHandler.Respond(
            HttpStatusCode.OK, """{"id":1,"slug":"launch-post","adminUrl":"https://cms.example/admin/1"}""");
        using var h = await BuildAsync(new FakeHttpClientFactory(handler));

        await RunToCompletionAsync(h,
            new HttpRequestActionSpec { Url = "https://cms.example/api/publish", Method = "POST", TimeoutSeconds = 5 },
            new ExecutePowerShellActionSpec
            {
                Script = "Write-Output \"admin=$($args[0])\"",
                Arguments = ["{http.body.adminUrl}"],
                TimeoutSeconds = 15,
                AbortOnFailure = false,
            },
            new AddCommentActionSpec { Content = "{powershell.stdout}", Author = "automation" });

        Assert.Equal("admin=https://cms.example/admin/1", Assert.Single(await CommentsAsync(h)));
    }

    // ── Source-level fallback: always runs, even with no PowerShell on PATH ──

    [Fact]
    public void ActionExecutor_publishes_powershell_stdout_and_exitCode_into_chain_values()
    {
        var src = File.ReadAllText(LocateRepoFile("GigaClaw.Core/Automation/ActionExecutor.cs"));
        Assert.Contains("\"powershell.stdout\"", src);
        Assert.Contains("\"powershell.exitCode\"", src);
    }

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
}
