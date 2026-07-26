namespace GigaClaw.Core.Models;

public sealed record AgentTeam(
    string Slug,
    string Name,
    string Description,
    string Icon,
    IReadOnlyList<string> AgentSlugs
);
