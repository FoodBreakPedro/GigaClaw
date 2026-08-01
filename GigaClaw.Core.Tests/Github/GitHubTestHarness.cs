using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using GigaClaw.Core.Automation.Policy;
using GigaClaw.Core.Github;
using GigaClaw.Core.Services;
using GigaClaw.Core.Tests.Helpers;
using Microsoft.Extensions.Logging.Abstractions;

namespace GigaClaw.Core.Tests.Github;

/// <summary>Records receipts instead of writing them, so a test can assert on the exact shape.</summary>
internal sealed class RecordingReceiptSink : IOutboundReceiptSink
{
    public List<(string Slug, int? TicketId, OutboundReceipt Receipt)> Receipts { get; } = [];

    public Task WriteAsync(string projectSlug, int? ticketId, OutboundReceipt receipt, CancellationToken ct = default)
    {
        Receipts.Add((projectSlug, ticketId, receipt));
        return Task.CompletedTask;
    }
}

/// <summary>
/// Shared wiring for the C7 GitHub-surface suites. Everything is real except the transport: the
/// <see cref="FakeHttpMessageHandler"/> means no test in this folder can reach the network, and the
/// <see cref="OutboundApprovalGate"/> is built the way production builds it — from the owner's
/// settings.json, re-read per call — so approval behaviour under test is the shipped behaviour.
/// </summary>
internal sealed class GitHubTestHarness : IDisposable
{
    public required TempDir Tmp { get; init; }
    public required ProjectService Projects { get; init; }
    public required TicketService Tickets { get; init; }
    public required MemberService Members { get; init; }
    public required AppSettingsService Settings { get; init; }
    public required GitHubApiClient Client { get; init; }
    public required GitHubIssueLinkStore Links { get; init; }
    public required GitHubIssueSyncService Sync { get; init; }
    public required GitHubPullRequestService PullRequests { get; init; }
    public required RecordingReceiptSink ReceiptSink { get; init; }
    public required FakeHttpMessageHandler Handler { get; init; }
    public required string Slug { get; init; }
    public required string Workspace { get; init; }

    /// <summary>Every outbound denial the policy layer produced in this harness, in order.</summary>
    public IReadOnlyList<(string Slug, int? TicketId, OutboundReceipt Receipt)> Receipts => ReceiptSink.Receipts;

    /// <summary>The PAT every fixture uses. Deliberately distinctive so a leak is unmistakable.</summary>
    public const string Token = "ghp_TESTTOKEN_must_never_reach_a_ticket_0123456789";

    public const string ApiBase = "https://api.github.test";
    public const string ApiHost = "api.github.test";

    public static GitHubProjectConfig Config(
        bool enabled = true,
        string label = "gigaclaw",
        bool commentOnDone = false,
        bool closeOnDone = false,
        IReadOnlyList<string>? ownerLogins = null,
        string gitRemote = "origin",
        string pullRequestBase = "main") => new()
        {
            Enabled = enabled,
            Owner = "acme",
            Repo = "widgets",
            ImportLabel = label,
            ImportStatus = "Backlog",
            CommentOnIssueWhenTicketDone = commentOnDone,
            CloseIssueWhenTicketDone = closeOnDone,
            DoneStatuses = ["Done"],
            OwnerLogins = ownerLogins ?? [],
            ApiBaseUrl = ApiBase,
            GitRemote = gitRemote,
            PullRequestBase = pullRequestBase,
        };

    /// <summary>
    /// The trusted owner action, merged into whatever is already in settings.json so it can be
    /// called before or after <see cref="ConfigureGitHub"/> without clobbering it.
    /// </summary>
    public void OwnerApproves(params string[] hosts)
    {
        var path = Path.Combine(Tmp.Path, "settings.json");
        var root = File.Exists(path)
            ? JsonNode.Parse(File.ReadAllText(path)) as JsonObject ?? new JsonObject()
            : new JsonObject();
        var array = new JsonArray();
        foreach (var host in hosts) array.Add(host);
        root["ApprovedOutboundHosts"] = array;
        File.WriteAllText(path, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }

    public void ConfigureGitHub(GitHubProjectConfig config, string? token = Token) =>
        Settings.ConfigureGitHub(Slug, config, token);

    public async Task<List<string>> TicketTextAsync()
    {
        var text = new List<string>();
        foreach (var summary in await Tickets.ListTicketsAsync(Slug))
        {
            var ticket = await Tickets.GetTicketAsync(Slug, summary.Id);
            if (ticket is null) continue;
            text.Add(ticket.Title);
            text.Add(ticket.Description ?? "");
            text.AddRange(ticket.Comments.Select(c => c.Content));
        }
        return text;
    }

    public void Dispose() => Tmp.Dispose();

    public static async Task<GitHubTestHarness> BuildAsync(FakeHttpMessageHandler handler)
    {
        var tmp = new TempDir();
        var projects = new ProjectService(tmp.Path);
        var project = await projects.CreateProjectAsync("github-surface-test");
        var workspace = projects.ResolveWorkspacePath(project);
        Directory.CreateDirectory(workspace);
        return Rebuild(tmp, projects, project.Slug, workspace, handler);
    }

    /// <summary>
    /// Rebuilds every service over the same data directory — the restart simulation. Nothing
    /// in-memory survives; if a second sync still declines to duplicate, the reason is the durable
    /// mapping table and nothing else.
    /// </summary>
    public GitHubTestHarness Restart(FakeHttpMessageHandler handler) =>
        Rebuild(Tmp, new ProjectService(Tmp.Path), Slug, Workspace, handler);

    private static GitHubTestHarness Rebuild(
        TempDir tmp, ProjectService projects, string slug, string workspace, FakeHttpMessageHandler handler)
    {
        var members = new MemberService(projects);
        var tickets = new TicketService(projects, members);
        var settings = new AppSettingsService(tmp.Path);
        var receipts = new RecordingReceiptSink();
        var gate = new OutboundApprovalGate(settings.GetApprovedOutboundHosts);
        var client = new GitHubApiClient(
            new FakeHttpClientFactory(handler), gate, receipts, NullLogger<GitHubApiClient>.Instance);
        var links = new GitHubIssueLinkStore(projects);

        return new GitHubTestHarness
        {
            Tmp = tmp,
            Projects = projects,
            Tickets = tickets,
            Members = members,
            Settings = settings,
            Client = client,
            Links = links,
            Sync = new GitHubIssueSyncService(settings, client, links, tickets, NullLogger<GitHubIssueSyncService>.Instance),
            PullRequests = new GitHubPullRequestService(
                settings, client, projects, tickets, gate, receipts,
                NullLogger<GitHubPullRequestService>.Instance),
            ReceiptSink = receipts,
            Handler = handler,
            Slug = slug,
            Workspace = workspace,
        };
    }
}

/// <summary>Scripted GitHub responses, matched on method + path so one handler can serve a pass.</summary>
internal sealed class GitHubApiScript
{
    private readonly List<(Func<HttpRequestMessage, bool> Match, HttpStatusCode Status, string Body)> _routes = [];

    public GitHubApiScript Get(string pathAndQueryPrefix, HttpStatusCode status, string body)
    {
        _routes.Add((r => r.Method == HttpMethod.Get
            && r.RequestUri!.PathAndQuery.StartsWith(pathAndQueryPrefix, StringComparison.Ordinal), status, body));
        return this;
    }

    public GitHubApiScript Post(string path, HttpStatusCode status, string body)
    {
        _routes.Add((r => r.Method == HttpMethod.Post && r.RequestUri!.AbsolutePath == path, status, body));
        return this;
    }

    public GitHubApiScript Patch(string path, HttpStatusCode status, string body)
    {
        _routes.Add((r => r.Method == HttpMethod.Patch && r.RequestUri!.AbsolutePath == path, status, body));
        return this;
    }

    public FakeHttpMessageHandler Build() => new((request, _) =>
    {
        foreach (var route in _routes)
        {
            if (!route.Match(request)) continue;
            return Task.FromResult(new HttpResponseMessage(route.Status)
            {
                Content = new StringContent(route.Body, System.Text.Encoding.UTF8, "application/json"),
            });
        }
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound)
        {
            Content = new StringContent("""{"message":"no scripted route"}""", System.Text.Encoding.UTF8, "application/json"),
        });
    });
}
