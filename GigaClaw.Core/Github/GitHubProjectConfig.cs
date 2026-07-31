namespace GigaClaw.Core.Github;

/// <summary>
/// The per-project GitHub surface configuration (C7/U5). Everything here is non-secret and safe to
/// return from the API or render in a UI — the personal access token is deliberately <b>not</b> a
/// member of this record. The token lives only in <c>AppSettingsService</c>
/// (<c>%APPDATA%/GigaClaw/settings.json</c>), the same trust anchor
/// <see cref="Automation.Policy.OutboundApprovalGate"/> uses: outside every workspace and therefore
/// outside every agent's write globs.
/// <para>
/// Local-first stays the default. An unconfigured project has no row here at all, and every entry
/// point short-circuits before it can reach the network.
/// </para>
/// </summary>
public sealed record GitHubProjectConfig
{
    /// <summary>Master switch. False (the default) means the whole surface is inert.</summary>
    public bool Enabled { get; init; }

    /// <summary>Repository owner (user or org), e.g. <c>anthropics</c>.</summary>
    public string Owner { get; init; } = "";

    /// <summary>Repository name, e.g. <c>claude-code</c>.</summary>
    public string Repo { get; init; } = "";

    /// <summary>Only issues carrying this label are imported. Empty imports nothing.</summary>
    public string ImportLabel { get; init; } = "gigaclaw";

    /// <summary>Column imported issues land in.</summary>
    public string ImportStatus { get; init; } = "Backlog";

    /// <summary>Post a comment on the issue when its ticket reaches a done status.</summary>
    public bool CommentOnIssueWhenTicketDone { get; init; }

    /// <summary>Close the issue when its ticket reaches a done status.</summary>
    public bool CloseIssueWhenTicketDone { get; init; }

    /// <summary>Ticket statuses that count as "the work is finished" for the round trip.</summary>
    public IReadOnlyList<string> DoneStatuses { get; init; } = ["Done"];

    /// <summary>
    /// GitHub logins whose PR review comments are treated as owner feedback (part 2). Empty means
    /// no login qualifies, so no comment can steer an agent — fail closed, on purpose.
    /// </summary>
    public IReadOnlyList<string> OwnerLogins { get; init; } = [];

    /// <summary>API root. Overridable for GitHub Enterprise; also what tests point at a fake.</summary>
    public string ApiBaseUrl { get; init; } = "https://api.github.com";

    /// <summary>True once the repository coordinates are usable.</summary>
    public bool HasRepository =>
        !string.IsNullOrWhiteSpace(Owner) && !string.IsNullOrWhiteSpace(Repo);

    /// <summary><c>owner/repo</c>, the key issue links are stored under.</summary>
    public string RepositoryKey => $"{Owner}/{Repo}";
}
