using System.Text.Json;
using GigaClaw.Core.Automation.Policy;
using GigaClaw.Core.Services;
using Microsoft.Extensions.Logging;

namespace GigaClaw.Core.Github;

/// <summary>
/// What one "publish this ticket's branch" attempt did. A refusal is <see cref="DryRun"/> rather
/// than an error, the same distinction <see cref="GitHubResponse"/> draws: the policy layer doing
/// its job is not a failure.
/// </summary>
public sealed record PullRequestResult(
    bool Opened,
    int? Number = null,
    string? HtmlUrl = null,
    bool AlreadyOpen = false,
    bool Pushed = false,
    bool DryRun = false,
    string? Error = null)
{
    /// <summary>The branch is on the remote and a pull request exists for it, new or not.</summary>
    public bool Published => Opened || AlreadyOpen;

    public static PullRequestResult Skipped(string reason) => new(false, Error: reason);
    public static PullRequestResult Refused(string reason, bool pushed = false) =>
        new(false, Pushed: pushed, DryRun: true, Error: reason);
    public static PullRequestResult Failed(string error, bool pushed = false) =>
        new(false, Pushed: pushed, Error: error);
}

/// <summary>
/// U6: the missing leg between R5 (a ticket's work lives on its own <c>ticket/&lt;id&gt;</c> branch
/// in a worktree) and C7's <c>githubCheckStatus</c> trigger (CI conclusions on a commit become
/// board events). Something has to put the branch where CI can see it and open the pull request the
/// checks hang off. That is all this service does.
/// <para>
/// <b>It follows C7's rules, not new ones.</b> The PAT is read from
/// <see cref="AppSettingsService"/> and never stored on the config record or a ticket; every HTTP
/// call goes through <see cref="GitHubApiClient"/> and therefore through the P3
/// <see cref="OutboundApprovalGate"/>; a refusal writes an <c>outbound-denial/v1</c> receipt rather
/// than being swallowed; an unconfigured project short-circuits before anything can leave the
/// process.
/// </para>
/// <para>
/// <b>The push is git's, and it is gated too.</b> The branch is pushed by the workspace's own
/// <c>git push</c>, so whatever credential helper the owner already configured for that remote is
/// what authenticates — GigaClaw never handles it. But a push is still outbound traffic, so the
/// remote's host is put through the same <see cref="OutboundApprovalGate"/> before git is invoked.
/// A remote with no host (a filesystem path, including the bare repository the U6 suite pushes to)
/// is not outbound traffic and is not gated; the receipt vocabulary would have nothing to name.
/// </para>
/// <para>
/// <b>Idempotence without a table.</b> Before creating anything the service asks GitHub whether a
/// pull request already exists for the branch (<c>GET /pulls?head=owner:branch&amp;state=all</c>).
/// That makes "do not open a second PR after a restart" a property of what GitHub says now rather
/// than of any state this process carries — the same reasoning
/// <see cref="GitHubIssueSyncService"/> uses for its import pass, and the reason a crash between
/// push and receipt costs nothing.
/// </para>
/// </summary>
public sealed class GitHubPullRequestService
{
    /// <summary>Receipt schema for the pull request this service opened.</summary>
    public const string ReceiptSchema = "github-pull-request/v1";

    private static readonly TimeSpan GitTimeout = TimeSpan.FromMinutes(2);

    private readonly AppSettingsService _settings;
    private readonly GitHubApiClient _client;
    private readonly ProjectService _projects;
    private readonly TicketService _tickets;
    private readonly OutboundApprovalGate _gate;
    private readonly IOutboundReceiptSink _receipts;
    private readonly ILogger<GitHubPullRequestService> _logger;

    public GitHubPullRequestService(
        AppSettingsService settings,
        GitHubApiClient client,
        ProjectService projects,
        TicketService tickets,
        OutboundApprovalGate gate,
        IOutboundReceiptSink receipts,
        ILogger<GitHubPullRequestService> logger)
    {
        _settings = settings;
        _client = client;
        _projects = projects;
        _tickets = tickets;
        _gate = gate;
        _receipts = receipts;
        _logger = logger;
    }

    /// <summary>
    /// Pushes the ticket's R5 worktree branch to the configured remote and opens (or re-finds) its
    /// pull request. Returns rather than throws for every "not configured" case: a project that
    /// never opted in must cost nothing and say so.
    /// </summary>
    public async Task<PullRequestResult> OpenForTicketAsync(
        string slug, int ticketId, CancellationToken ct = default)
    {
        var config = _settings.GetGitHubConfig(slug);
        if (config is null || !config.Enabled)
            return PullRequestResult.Skipped("GitHub integration is not enabled for this project.");
        if (!config.HasRepository)
            return PullRequestResult.Skipped("No GitHub repository configured (owner/repo).");

        var token = _settings.GetGitHubToken(slug);
        if (string.IsNullOrWhiteSpace(token))
            return PullRequestResult.Skipped("No GitHub token configured for this project.");

        var ticket = await _tickets.GetTicketAsync(slug, ticketId);
        if (ticket is null)
            return PullRequestResult.Skipped($"Ticket #{ticketId} does not exist.");
        if (string.IsNullOrWhiteSpace(ticket.WorktreeBranch))
        {
            return PullRequestResult.Skipped(
                $"Ticket #{ticketId} has no recorded worktree branch — dispatch it with " +
                "isolation: \"worktree\" before opening a pull request.");
        }

        var project = await _projects.GetProjectAsync(slug);
        if (project is null) return PullRequestResult.Skipped("The project no longer exists.");
        var workspace = _projects.ResolveWorkspacePath(project);

        var branch = ticket.WorktreeBranch!;
        var push = await PushBranchAsync(slug, ticketId, workspace, config, branch, ct);
        if (!push.Pushed) return push;

        var existing = await FindOpenPullRequestAsync(slug, ticketId, config, token!, branch, ct);
        if (existing.DryRun || existing.Error is not null)
            return existing with { Pushed = true };
        if (existing.Number is int already)
            return new PullRequestResult(false, already, existing.HtmlUrl, AlreadyOpen: true, Pushed: true);

        return await CreatePullRequestAsync(slug, ticket.Id, ticket.Title, config, token!, branch, ct);
    }

    // ── push ────────────────────────────────────────────────────────────────

    private async Task<PullRequestResult> PushBranchAsync(
        string slug, int ticketId, string workspace, GitHubProjectConfig config, string branch, CancellationToken ct)
    {
        var remote = string.IsNullOrWhiteSpace(config.GitRemote) ? "origin" : config.GitRemote.Trim();

        var remoteUrl = await RunGitAsync(workspace, $"remote get-url {remote}", ct);
        if (remoteUrl.exitCode != 0)
            return PullRequestResult.Skipped($"The workspace has no git remote named '{remote}'.");
        var url = remoteUrl.stdout.Trim();

        // A push is outbound traffic whenever it names a host, so it is subject to the same owner
        // approval an API call is. Evaluated as https://<host> so the one host-approval rule in
        // OutboundApprovalGate governs both surfaces rather than a second, weaker copy of it.
        if (RemoteHost(url) is string host)
        {
            var approval = _gate.Evaluate($"https://{host}");
            if (!approval.MaySend)
            {
                var reason = approval.Reason ?? "no trusted outbound approval";
                await _receipts.WriteAsync(slug, ticketId, new OutboundReceipt(
                    Agent: "github-pull-request",
                    Action: "gitPush",
                    Target: SanitizeRemote(url),
                    Host: host,
                    Reason: reason), ct);
                return PullRequestResult.Refused(reason);
            }
        }

        var push = await RunGitAsync(workspace, $"push {remote} {branch}", ct);
        if (push.exitCode != 0)
        {
            // stderr from a push can echo the remote URL, which can carry userinfo.
            return PullRequestResult.Failed($"git push {remote} {branch} failed: {SanitizeRemote(push.stderr).Trim()}");
        }

        _logger.LogInformation("[{Slug}] pushed {Branch} to {Remote} for ticket #{Id}", slug, branch, remote, ticketId);
        return new PullRequestResult(false, Pushed: true);
    }

    /// <summary>
    /// The host a git remote points at, or null when the remote is not a network destination (a
    /// filesystem path or a <c>file://</c> URL). Handles both URL remotes and git's scp-like
    /// <c>user@host:path</c> form, which is not a URI and would otherwise slip past the gate.
    /// </summary>
    internal static string? RemoteHost(string? remoteUrl)
    {
        var url = remoteUrl?.Trim();
        if (string.IsNullOrEmpty(url)) return null;

        if (Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme != Uri.UriSchemeFile)
            return string.IsNullOrEmpty(uri.Host) ? null : uri.Host;

        // scp-like: [user@]host:path — a colon before the first slash, and never a Windows drive.
        var colon = url.IndexOf(':');
        if (colon <= 0) return null;
        var slash = url.IndexOf('/');
        if (slash >= 0 && slash < colon) return null;
        var authority = url[..colon];
        var at = authority.LastIndexOf('@');
        var host = at >= 0 ? authority[(at + 1)..] : authority;
        return host.Length > 1 && host.Contains('.') ? host : null;
    }

    /// <summary>
    /// Strips <c>user:password@</c> userinfo from a remote URL. Receipts land on tickets, and a
    /// remote configured as <c>https://x-access-token:&lt;PAT&gt;@github.com/…</c> would otherwise
    /// put the token on the board — the exact outcome C7's containment rule forbids.
    /// </summary>
    internal static string SanitizeRemote(string? text)
    {
        if (string.IsNullOrEmpty(text)) return text ?? "";
        return System.Text.RegularExpressions.Regex.Replace(
            text, @"(?<scheme>[a-zA-Z][a-zA-Z0-9+.\-]*://)[^/@\s]+@", "${scheme}");
    }

    // ── pull request ────────────────────────────────────────────────────────

    private async Task<PullRequestResult> FindOpenPullRequestAsync(
        string slug, int ticketId, GitHubProjectConfig config, string token, string branch, CancellationToken ct)
    {
        var url = $"{config.ApiBaseUrl.TrimEnd('/')}/repos/{config.Owner}/{config.Repo}/pulls"
                + $"?head={Uri.EscapeDataString($"{config.Owner}:{branch}")}&state=all&per_page=1";

        var response = await _client.SendAsync(
            new GitHubRequest(slug, HttpMethod.Get, url, token, TicketId: ticketId, Actor: "github-pull-request"), ct);
        if (response.DryRun) return PullRequestResult.Refused(response.Error ?? "outbound request refused by policy.");
        if (!response.Success) return PullRequestResult.Failed(response.Error ?? "GitHub request failed.");

        try
        {
            using var document = JsonDocument.Parse(response.Body);
            if (document.RootElement.ValueKind != JsonValueKind.Array) return new PullRequestResult(false);
            foreach (var element in document.RootElement.EnumerateArray())
            {
                if (ReadPullRequest(element) is not { } found) continue;
                return new PullRequestResult(false, found.Number, found.HtmlUrl);
            }
        }
        catch (JsonException exception)
        {
            _logger.LogWarning(exception, "[{Slug}] GitHub pull-request list was not readable JSON", slug);
            return PullRequestResult.Failed("GitHub returned a response that could not be parsed.");
        }

        return new PullRequestResult(false);
    }

    private async Task<PullRequestResult> CreatePullRequestAsync(
        string slug, int ticketId, string ticketTitle, GitHubProjectConfig config,
        string token, string branch, CancellationToken ct)
    {
        var baseBranch = string.IsNullOrWhiteSpace(config.PullRequestBase) ? "main" : config.PullRequestBase.Trim();
        var body = JsonSerializer.Serialize(new
        {
            title = $"{ticketTitle} (ticket-{ticketId})",
            head = branch,
            @base = baseBranch,
            // The ticket reference is deliberately in the body: it is what binds a later PR comment
            // or check conclusion back to this ticket (TicketReference.Find), so the round trip does
            // not need a second mapping table to know whose CI result it is looking at.
            body = $"Opened by GigaClaw for ticket-{ticketId}.\n\nBranch `{branch}` → `{baseBranch}`.",
        });

        var url = $"{config.ApiBaseUrl.TrimEnd('/')}/repos/{config.Owner}/{config.Repo}/pulls";
        var response = await _client.SendAsync(
            new GitHubRequest(slug, HttpMethod.Post, url, token, body, ticketId, "github-pull-request"), ct);
        if (response.DryRun)
            return PullRequestResult.Refused(response.Error ?? "outbound request refused by policy.", pushed: true);
        if (!response.Success)
            return PullRequestResult.Failed(response.Error ?? "GitHub refused to create the pull request.", pushed: true);

        int? number = null;
        string? htmlUrl = null;
        try
        {
            using var document = JsonDocument.Parse(response.Body);
            if (ReadPullRequest(document.RootElement) is { } created)
            {
                number = created.Number;
                htmlUrl = created.HtmlUrl;
            }
        }
        catch (JsonException exception)
        {
            _logger.LogWarning(exception, "[{Slug}] the created pull request was not readable JSON", slug);
        }

        var receipt = JsonSerializer.Serialize(new
        {
            schema = ReceiptSchema,
            ticketId,
            branch,
            baseBranch,
            pullRequest = number,
            htmlUrl,
        });
        try { await _tickets.AddCommentAsync(slug, ticketId, receipt, "automation"); }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "[{Slug}] failed to write the pull-request receipt for ticket #{Id}", slug, ticketId);
        }

        _logger.LogInformation(
            "[{Slug}] opened pull request #{Number} for ticket #{Id} ({Branch} → {Base})",
            slug, number, ticketId, branch, baseBranch);
        return new PullRequestResult(true, number, htmlUrl, Pushed: true);
    }

    private static (int Number, string? HtmlUrl)? ReadPullRequest(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object) return null;
        if (!element.TryGetProperty("number", out var number) || !number.TryGetInt32(out var parsed)) return null;
        var htmlUrl = element.TryGetProperty("html_url", out var html) && html.ValueKind == JsonValueKind.String
            ? html.GetString()
            : null;
        return (parsed, htmlUrl);
    }

    private static async Task<(int exitCode, string stdout, string stderr)> RunGitAsync(
        string cwd, string args, CancellationToken ct)
    {
        var result = await ProcessRunner.RunAsync("git", args, cwd, GitTimeout, ct: ct);
        return (result.ExitCode ?? -1, result.Stdout, result.TimedOut ? "git timed out after 2 minutes" : result.Stderr);
    }
}
