using System.Text.RegularExpressions;
using System.Text.Json.Serialization;

namespace GigaClaw.Core.Models;

[JsonConverter(typeof(JsonStringEnumConverter<AgentHarness>))]
public enum AgentHarness
{
    Claude,
    Codex,
}

public class Member
{
    public int Id { get; set; }
    public required string Name { get; set; }
    public string Slug { get; set; } = "";
    public string? DefaultModel { get; set; }
    public AgentHarness Harness { get; set; } = AgentHarness.Claude;

    public static string ToSlug(string name) =>
        Regex.Replace(name.Trim().ToLowerInvariant(), @"[\s_]+", "-");
}
