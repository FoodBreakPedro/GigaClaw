using System.Text.Encodings.Web;
using System.Text.Json;

namespace GigaClaw.Core.Automation;

/// <summary>
/// Parses the AD-7 draft format used by content-pipeline tickets: a ticket <c>Description</c>
/// holding a <c>---</c>-fenced, YAML-ish frontmatter header followed by a markdown body, e.g.
/// <code>
/// ---
/// title: ...
/// slug: ...
/// excerpt: ...
/// contentType: article
/// seo:
///   title: ...
///   description: ...
///   primaryKeyword: ...
/// imagePrompt: ...
/// ---
/// &lt;markdown body&gt;
/// </code>
/// <para>
/// Hand-rolled on purpose: the format is intentionally small (flat scalars plus one level of
/// nesting under <c>seo:</c>), so pulling in a full YAML package for it would be overkill. The
/// parser is deliberately tolerant of missing optional keys — only the fences and <c>title</c>
/// are required — but rejects anything it cannot make sense of via <see cref="TryParse"/>'s
/// <c>error</c> output, so a caller building a CMS payload from it can refuse to dispatch a
/// malformed draft instead of POSTing garbage.
/// </para>
/// </summary>
public sealed class DraftFrontmatter
{
    public string Title { get; init; } = "";
    public string Slug { get; init; } = "";
    public string Excerpt { get; init; } = "";
    public string ContentType { get; init; } = "";
    public string ImagePrompt { get; init; } = "";
    public string SeoTitle { get; init; } = "";
    public string SeoDescription { get; init; } = "";
    public string SeoPrimaryKeyword { get; init; } = "";
    public string Body { get; init; } = "";

    /// <summary>
    /// Attempts to parse <paramref name="description"/>. Fails when the opening or closing
    /// <c>---</c> fence is missing, or when <c>title</c> is absent/blank — those three are what a
    /// caller needs to safely build a downstream payload. Every other field is optional and
    /// defaults to <c>""</c> when not present.
    /// </summary>
    public static bool TryParse(string? description, out DraftFrontmatter? draft, out string? error)
    {
        draft = null;
        error = null;

        if (string.IsNullOrWhiteSpace(description))
        {
            error = "ticket description is empty";
            return false;
        }

        var lines = description.Replace("\r\n", "\n").Replace("\r", "\n").Split('\n');

        var i = 0;
        while (i < lines.Length && lines[i].Trim().Length == 0) i++;
        if (i >= lines.Length || lines[i].Trim() != "---")
        {
            error = "missing opening '---' frontmatter fence";
            return false;
        }
        i++; // past the opening fence

        var flat = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var seo = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        string? nestedKey = null;
        var closeIndex = -1;

        for (; i < lines.Length; i++)
        {
            var line = lines[i];
            if (line.Trim() == "---") { closeIndex = i; break; }
            if (line.Trim().Length == 0) continue;

            var indented = line.Length > 0 && (line[0] == ' ' || line[0] == '\t');
            var trimmed = line.Trim();
            var colon = trimmed.IndexOf(':');
            if (colon < 0) continue; // tolerate stray non-key lines rather than failing the parse

            var key = trimmed[..colon].Trim();
            var value = StripQuotes(trimmed[(colon + 1)..].Trim());

            if (indented)
            {
                // Only one level of nesting is supported, and only under "seo" — anything else is
                // ignored rather than failing the whole parse.
                if (string.Equals(nestedKey, "seo", StringComparison.OrdinalIgnoreCase))
                    seo[key] = value;
                continue;
            }

            // A flush-left "key:" with no inline value opens a nested block (only "seo" is
            // meaningful here); a flush-left "key: value" closes any nested block that was open.
            nestedKey = value.Length == 0 ? key : null;
            if (value.Length > 0) flat[key] = value;
        }

        if (closeIndex < 0)
        {
            error = "missing closing '---' frontmatter fence";
            return false;
        }

        if (!flat.TryGetValue("title", out var title) || string.IsNullOrWhiteSpace(title))
        {
            error = "missing required 'title' field in frontmatter";
            return false;
        }

        var body = closeIndex + 1 < lines.Length
            ? string.Join("\n", lines.Skip(closeIndex + 1))
            : "";
        body = body.Trim('\n');

        draft = new DraftFrontmatter
        {
            Title = title,
            Slug = flat.GetValueOrDefault("slug", ""),
            Excerpt = flat.GetValueOrDefault("excerpt", ""),
            ContentType = flat.GetValueOrDefault("contentType", ""),
            ImagePrompt = flat.GetValueOrDefault("imagePrompt", ""),
            SeoTitle = seo.GetValueOrDefault("title", ""),
            SeoDescription = seo.GetValueOrDefault("description", ""),
            SeoPrimaryKeyword = seo.GetValueOrDefault("primaryKeyword", ""),
            Body = body,
        };
        return true;
    }

    private static string StripQuotes(string s)
    {
        if (s.Length >= 2 && ((s[0] == '"' && s[^1] == '"') || (s[0] == '\'' && s[^1] == '\'')))
            return s[1..^1];
        return s;
    }

    /// <summary>
    /// Flattens this draft into <c>draft.*</c> placeholder values — <c>draft.title</c>,
    /// <c>draft.slug</c>, <c>draft.excerpt</c>, <c>draft.contentType</c>, <c>draft.imagePrompt</c>,
    /// <c>draft.seo.title</c>, <c>draft.seo.description</c>, <c>draft.seo.primaryKeyword</c>,
    /// <c>draft.body</c> — each already JSON-string-escaped (quotes, backslashes, newlines,
    /// control characters) via <see cref="JsonEscape"/>. Callers splice these directly between a
    /// literal pair of <c>"</c> in a JSON <c>BodyTemplate</c>; they must never be used un-escaped
    /// in a JSON context, and are not appropriate for a raw URL or header value.
    /// </summary>
    public IReadOnlyDictionary<string, string> ToJsonEscapedValues() => new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["draft.title"] = JsonEscape(Title),
        ["draft.slug"] = JsonEscape(Slug),
        ["draft.excerpt"] = JsonEscape(Excerpt),
        ["draft.contentType"] = JsonEscape(ContentType),
        ["draft.imagePrompt"] = JsonEscape(ImagePrompt),
        ["draft.seo.title"] = JsonEscape(SeoTitle),
        ["draft.seo.description"] = JsonEscape(SeoDescription),
        ["draft.seo.primaryKeyword"] = JsonEscape(SeoPrimaryKeyword),
        ["draft.body"] = JsonEscape(Body),
    };

    // Relaxed encoder: this text is destined for a JSON API body, not for embedding in HTML, so
    // there is no reason to additionally \u-escape '<', '>', '&', or non-ASCII letters the way
    // the default (HTML-safe) encoder does. Only what JSON syntax actually requires is escaped.
    private static readonly JsonSerializerOptions JsonEscapeOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>
    /// JSON-string-escapes <paramref name="value"/> (quotes, backslashes, control characters) —
    /// without the surrounding quotes, since the BodyTemplate already supplies those.
    /// </summary>
    internal static string JsonEscape(string? value)
    {
        if (string.IsNullOrEmpty(value)) return "";
        var json = JsonSerializer.Serialize(value, JsonEscapeOptions);
        return json[1..^1]; // strip the wrapping quotes JsonSerializer always adds for a string
    }
}
