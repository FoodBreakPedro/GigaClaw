using System.Text.Json;
using System.Text.Json.Serialization;
using GigaClaw.Core.Automation;

namespace GigaClaw.Core.Models;

/// <summary>
/// One team as written in a <c>teams.json</c> document. It is the data form of
/// <see cref="TeamDefinition"/>: the concise <see cref="AgentSlugs"/> spelling covers the pure
/// member filters (one seat per agent, in declaration order), and the full
/// <see cref="Roles"/> / <see cref="TaskGraph"/> spelling covers an executable team, so both live
/// in one file and neither needs a C# edit to exist.
/// </summary>
public sealed record TeamSeedEntry(string Slug, string Name, string Description, string Icon)
{
    /// <summary>Membership shorthand: one seat per agent, role id = agent slug.</summary>
    public IReadOnlyList<string> AgentSlugs { get; init; } = [];

    /// <summary>Explicit seats. When present it wins over <see cref="AgentSlugs"/>.</summary>
    public IReadOnlyList<TeamRole>? Roles { get; init; }

    public IReadOnlyList<ConditionSpec> EntryConditions { get; init; } = [];
    public IReadOnlyList<TeamTaskTemplate> TaskGraph { get; init; } = [];
    public TeamJoinPolicy? JoinPolicy { get; init; }
    public string? SynthesizerRole { get; init; }

    /// <summary>C8: see <see cref="TeamDefinition.DedupeFindings"/>.</summary>
    public bool DedupeFindings { get; init; }

    /// <summary>C8: see <see cref="TeamDefinition.RequireEvidenceCitingArbitration"/>.</summary>
    public bool RequireEvidenceCitingArbitration { get; init; }

    public TeamDefinition ToDefinition() =>
        new(Slug, Name, Description, Icon)
        {
            Roles = Roles ?? [.. AgentSlugs.Select(agentSlug => new TeamRole(agentSlug, agentSlug))],
            EntryConditions = EntryConditions,
            TaskGraph = TaskGraph,
            JoinPolicy = JoinPolicy ?? TeamJoinPolicy.AllDone,
            SynthesizerRole = SynthesizerRole,
            DedupeFindings = DedupeFindings,
            RequireEvidenceCitingArbitration = RequireEvidenceCitingArbitration
        };

    public static TeamSeedEntry FromDefinition(TeamDefinition definition) =>
        new(definition.Slug, definition.Name, definition.Description, definition.Icon)
        {
            Roles = definition.Roles,
            EntryConditions = definition.EntryConditions,
            TaskGraph = definition.TaskGraph,
            JoinPolicy = definition.JoinPolicy,
            SynthesizerRole = definition.SynthesizerRole,
            DedupeFindings = definition.DedupeFindings,
            RequireEvidenceCitingArbitration = definition.RequireEvidenceCitingArbitration
        };
}

/// <summary>
/// A parsed <c>teams.json</c>. <see cref="TeamMembership"/> maps a team slug to agent slugs that
/// are <b>added</b> to it — the mechanism a pack uses to join a team it does not own, which is why
/// it can only add and never remove.
/// </summary>
public sealed record TeamSeedDocument
{
    public int SchemaVersion { get; init; } = TeamSeed.SupportedSchemaVersion;
    public IReadOnlyList<TeamSeedEntry> Teams { get; init; } = [];
    public IReadOnlyDictionary<string, IReadOnlyList<string>> TeamMembership { get; init; } =
        new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// Reads and composes <c>teams.json</c> documents. Team definitions are <b>data, not code</b>:
/// the built-in teams ship as <c>ProjectTemplate/Agents/teams.json</c>, which Initialize writes to
/// <c>&lt;workspace&gt;/.agents/teams.json</c>, and anything that composes into that file — a pack,
/// or the owner — contributes a team or a membership without recompiling <c>GigaClaw.Core</c>.
/// <para>
/// Parsing is deliberately split in two. <see cref="Parse"/> throws, so a tool or a test sees a
/// bad file; <see cref="TryParse"/> degrades to null, so a hand-edited workspace file can never
/// take the team filter down — it falls back to the built-ins instead.
/// </para>
/// </summary>
public static class TeamSeed
{
    public const int SupportedSchemaVersion = 1;

    /// <summary>File name under <c>.agents/</c>, and the embedded template resource name.</summary>
    public const string FileName = "teams.json";

    internal static readonly JsonSerializerOptions Json = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
        Converters = { new JsonStringEnumConverter() }
    };

    /// <summary>Workspace-relative location of the composed roster.</summary>
    public static string WorkspacePath(string workspacePath) =>
        Path.Combine(workspacePath, ".agents", FileName);

    /// <summary>
    /// Parses one document. Both shapes are accepted: the object form
    /// (<c>{ "schemaVersion": 1, "teams": [...], "teamMembership": {...} }</c>) that core ships, and
    /// the bare array form that a pack fragment may use.
    /// </summary>
    public static TeamSeedDocument Parse(string json, string source)
    {
        if (string.IsNullOrWhiteSpace(json))
            throw new TeamDefinitionException("teams_unreadable", $"Team roster '{source}' is empty.");

        JsonDocument document;
        try { document = JsonDocument.Parse(json, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true }); }
        catch (JsonException exception)
        {
            throw new TeamDefinitionException("teams_unreadable", $"Team roster '{source}' is not valid JSON: {exception.Message}");
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind == JsonValueKind.Array)
                return new TeamSeedDocument { Teams = ReadTeams(root, source) };

            if (root.ValueKind != JsonValueKind.Object)
                throw new TeamDefinitionException("teams_unreadable", $"Team roster '{source}' must be an object or an array.");

            var schemaVersion = root.TryGetProperty("schemaVersion", out var version) && version.TryGetInt32(out var parsed)
                ? parsed
                : SupportedSchemaVersion;
            // A higher schemaVersion is refused rather than best-effort parsed: System.Text.Json
            // silently drops unknown members, so a newer roster would load as a quietly wrong one.
            if (schemaVersion > SupportedSchemaVersion)
                throw new TeamDefinitionException(
                    "teams_schema_unsupported",
                    $"Team roster '{source}' declares schemaVersion {schemaVersion}; this build understands {SupportedSchemaVersion}.");

            var teams = root.TryGetProperty("teams", out var teamsElement)
                ? ReadTeams(teamsElement, source)
                : [];

            var membership = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
            if (root.TryGetProperty("teamMembership", out var membershipElement) &&
                membershipElement.ValueKind == JsonValueKind.Object)
            {
                foreach (var property in membershipElement.EnumerateObject())
                {
                    if (property.Value.ValueKind != JsonValueKind.Array) continue;
                    membership[property.Name] =
                    [
                        .. property.Value.EnumerateArray()
                            .Where(item => item.ValueKind == JsonValueKind.String)
                            .Select(item => item.GetString()!)
                            .Where(slug => !string.IsNullOrWhiteSpace(slug))
                    ];
                }
            }

            return new TeamSeedDocument
            {
                SchemaVersion = schemaVersion,
                Teams = teams,
                TeamMembership = membership
            };
        }
    }

    /// <summary>Non-throwing <see cref="Parse"/>. Returns null for anything unreadable.</summary>
    public static TeamSeedDocument? TryParse(string json, string source)
    {
        try { return Parse(json, source); }
        catch (TeamDefinitionException) { return null; }
    }

    private static IReadOnlyList<TeamSeedEntry> ReadTeams(JsonElement element, string source)
    {
        if (element.ValueKind != JsonValueKind.Array)
            throw new TeamDefinitionException("teams_unreadable", $"Team roster '{source}' has a non-array 'teams'.");

        var entries = new List<TeamSeedEntry>();
        foreach (var item in element.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object) continue;
            TeamSeedEntry? entry;
            try { entry = item.Deserialize<TeamSeedEntry>(Json); }
            catch (JsonException exception)
            {
                throw new TeamDefinitionException("teams_unreadable", $"Team roster '{source}' has an unreadable team: {exception.Message}");
            }

            if (entry is null || string.IsNullOrWhiteSpace(entry.Slug))
                throw new TeamDefinitionException("teams_unreadable", $"Team roster '{source}' has a team without a slug.");
            entries.Add(entry);
        }

        return entries;
    }

    /// <summary>
    /// Composes documents in order into the definition list a project resolves teams against.
    /// Teams union by slug and a duplicate slug is a <b>hard error</b> — two contributors claiming
    /// one team is a packaging bug, and silently letting the last one win would make the roster
    /// depend on composition order. <c>teamMembership</c> is applied after every team exists, so a
    /// contributor can join a team declared by a later document, and it only ever adds seats.
    /// </summary>
    public static IReadOnlyList<TeamDefinition> Compose(params IEnumerable<TeamSeedDocument> documents)
    {
        var order = new List<string>();
        var bySlug = new Dictionary<string, TeamDefinition>(StringComparer.OrdinalIgnoreCase);
        // Materialized because the list is walked twice — teams first, then memberships.
        var all = documents as IReadOnlyList<TeamSeedDocument> ?? [.. documents];

        foreach (var document in all)
        {
            foreach (var entry in document.Teams)
            {
                if (bySlug.ContainsKey(entry.Slug))
                    throw new TeamDefinitionException(
                        "team_duplicate",
                        $"Team '{entry.Slug}' is declared more than once; team slugs are one flat namespace.");
                bySlug[entry.Slug] = entry.ToDefinition();
                order.Add(entry.Slug);
            }
        }

        foreach (var document in all)
        {
            foreach (var (teamSlug, agentSlugs) in document.TeamMembership)
            {
                if (!bySlug.TryGetValue(teamSlug, out var definition))
                    throw new TeamDefinitionException(
                        "team_membership_unknown",
                        $"Membership was declared for team '{teamSlug}', which no roster defines.");

                var seats = definition.Roles.ToList();
                foreach (var agentSlug in agentSlugs)
                {
                    if (seats.Any(role => string.Equals(role.AgentSlug, agentSlug, StringComparison.OrdinalIgnoreCase)))
                        continue;
                    seats.Add(new TeamRole(agentSlug, agentSlug));
                }
                bySlug[teamSlug] = definition with { Roles = seats };
            }
        }

        var composed = order.Select(slug => bySlug[slug]).ToArray();
        foreach (var definition in composed)
        {
            var problems = definition.Validate();
            if (problems.Count > 0)
                throw new TeamDefinitionException(
                    "definition_invalid",
                    $"Team '{definition.Slug}' is invalid: {string.Join(" ", problems)}");
        }

        return composed;
    }

    /// <summary>Serializes definitions back to the object form, for tooling and tests.</summary>
    public static string Write(IEnumerable<TeamDefinition> definitions) =>
        JsonSerializer.Serialize(
            new TeamSeedDocument { Teams = [.. definitions.Select(TeamSeedEntry.FromDefinition)] },
            new JsonSerializerOptions(Json) { WriteIndented = true });
}
