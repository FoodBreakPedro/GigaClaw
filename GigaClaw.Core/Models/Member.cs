using System.Text.RegularExpressions;

namespace GigaClaw.Core.Models;

public class Member
{
    public int Id { get; set; }
    public required string Name { get; set; }
    public string Slug { get; set; } = "";
    public string? DefaultModel { get; set; }

    public static string ToSlug(string name) =>
        Regex.Replace(name.Trim().ToLowerInvariant(), @"[\s_]+", "-");
}
