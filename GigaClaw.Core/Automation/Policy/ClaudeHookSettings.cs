using System.Text.Json;

namespace GigaClaw.Core.Automation.Policy;

/// <summary>
/// Creates and validates the narrow Claude settings schema used by the runtime
/// policy bridge. Keeping validation here lets the runner and mock agree on the
/// exact transport contract without accepting arbitrary settings.
/// </summary>
public static class ClaudeHookSettings
{
    public const string PreToolUseMatcher =
        "Write|Edit|NotebookEdit|Bash|WebFetch|WebSearch";

    public static string Serialize(Uri endpoint)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        ValidateEndpoint(endpoint);

        return JsonSerializer.Serialize(new
        {
            hooks = new
            {
                UserPromptSubmit = new[]
                {
                    new
                    {
                        hooks = new[]
                        {
                            new
                            {
                                type = "http",
                                url = endpoint.AbsoluteUri,
                                timeout = 5,
                            },
                        },
                    },
                },
                PreToolUse = new[]
                {
                    new
                    {
                        matcher = PreToolUseMatcher,
                        hooks = new[]
                        {
                            new
                            {
                                type = "http",
                                url = endpoint.AbsoluteUri,
                                timeout = 5,
                            },
                        },
                    },
                },
            },
        });
    }

    public static async Task<Uri> ReadAndValidateAsync(
        string settingsPath,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(settingsPath);

        await using var stream = File.OpenRead(settingsPath);
        using var document = await JsonDocument.ParseAsync(
            stream,
            cancellationToken: cancellationToken);
        return Validate(document.RootElement);
    }

    public static Uri Validate(JsonElement root)
    {
        RequireObject(root, "$");
        RejectUnknownProperties(root, "$", "hooks");

        var hooks = RequireProperty(root, "hooks", "$");
        RequireObject(hooks, "$.hooks");
        RejectUnknownProperties(hooks, "$.hooks", "UserPromptSubmit", "PreToolUse");

        var acknowledgement = RequireProperty(hooks, "UserPromptSubmit", "$.hooks");
        RequireArray(acknowledgement, "$.hooks.UserPromptSubmit", expectedCount: 1);
        var acknowledgementGroup = acknowledgement[0];
        RequireObject(acknowledgementGroup, "$.hooks.UserPromptSubmit[0]");
        RejectUnknownProperties(
            acknowledgementGroup,
            "$.hooks.UserPromptSubmit[0]",
            "hooks");
        var acknowledgementEndpoint = ValidateHttpHandler(
            RequireProperty(
                acknowledgementGroup,
                "hooks",
                "$.hooks.UserPromptSubmit[0]"),
            "$.hooks.UserPromptSubmit[0].hooks");

        var preToolUse = RequireProperty(hooks, "PreToolUse", "$.hooks");
        RequireArray(preToolUse, "$.hooks.PreToolUse", expectedCount: 1);
        var group = preToolUse[0];
        RequireObject(group, "$.hooks.PreToolUse[0]");
        RejectUnknownProperties(
            group,
            "$.hooks.PreToolUse[0]",
            "matcher",
            "hooks");

        var matcher = RequireString(
            RequireProperty(group, "matcher", "$.hooks.PreToolUse[0]"),
            "$.hooks.PreToolUse[0].matcher");
        if (!string.Equals(matcher, PreToolUseMatcher, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"$.hooks.PreToolUse[0].matcher must be '{PreToolUseMatcher}'.");
        }

        var endpoint = ValidateHttpHandler(
            RequireProperty(group, "hooks", "$.hooks.PreToolUse[0]"),
            "$.hooks.PreToolUse[0].hooks");
        if (endpoint != acknowledgementEndpoint)
        {
            throw new InvalidDataException(
                "UserPromptSubmit and PreToolUse must use the same policy hook endpoint.");
        }
        return endpoint;
    }

    private static Uri ValidateHttpHandler(JsonElement handlers, string path)
    {
        RequireArray(handlers, path, expectedCount: 1);
        var handler = handlers[0];
        RequireObject(handler, $"{path}[0]");
        RejectUnknownProperties(
            handler,
            $"{path}[0]",
            "type",
            "url",
            "timeout");

        var type = RequireString(
            RequireProperty(handler, "type", $"{path}[0]"),
            $"{path}[0].type");
        if (!string.Equals(type, "http", StringComparison.Ordinal))
            throw new InvalidDataException($"{path}[0].type must be 'http'.");

        var url = RequireString(
            RequireProperty(handler, "url", $"{path}[0]"),
            $"{path}[0].url");
        if (!Uri.TryCreate(url, UriKind.Absolute, out var endpoint))
            throw new InvalidDataException($"{path}[0].url must be an absolute URI.");
        ValidateEndpoint(endpoint);

        var timeout = RequireProperty(handler, "timeout", $"{path}[0]");
        if (timeout.ValueKind != JsonValueKind.Number ||
            !timeout.TryGetInt32(out var timeoutSeconds) ||
            timeoutSeconds is < 1 or > 30)
        {
            throw new InvalidDataException(
                $"{path}[0].timeout must be an integer from 1 through 30.");
        }

        return endpoint;
    }

    internal static async Task<string> WriteValidatedFileAsync(
        string runId,
        Uri endpoint,
        CancellationToken cancellationToken)
    {
        var directory = Path.Combine(Path.GetTempPath(), "gigaclaw-policy-hooks");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, $"{runId}-{Guid.NewGuid():N}.settings.json");
        var json = Serialize(endpoint);
        await File.WriteAllTextAsync(path, json, cancellationToken);

        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);

        _ = await ReadAndValidateAsync(path, cancellationToken);
        return path;
    }

    private static void ValidateEndpoint(Uri endpoint)
    {
        if (!string.Equals(endpoint.Scheme, Uri.UriSchemeHttp, StringComparison.Ordinal) ||
            !endpoint.IsLoopback ||
            endpoint.Port <= 0 ||
            endpoint.Port > 65535 ||
            string.IsNullOrWhiteSpace(endpoint.AbsolutePath) ||
            endpoint.AbsolutePath == "/")
        {
            throw new InvalidDataException(
                "Policy hook endpoint must be an HTTP loopback URI with an explicit port and non-root path.");
        }
    }

    private static JsonElement RequireProperty(JsonElement element, string name, string path)
    {
        if (!element.TryGetProperty(name, out var property))
            throw new InvalidDataException($"{path}.{name} is required.");
        return property;
    }

    private static string RequireString(JsonElement element, string path)
    {
        if (element.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(element.GetString()))
        {
            throw new InvalidDataException($"{path} must be a non-empty string.");
        }
        return element.GetString()!;
    }

    private static void RequireObject(JsonElement element, string path)
    {
        if (element.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException($"{path} must be a JSON object.");
    }

    private static void RequireArray(JsonElement element, string path, int expectedCount)
    {
        if (element.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException($"{path} must be a JSON array.");
        if (element.GetArrayLength() != expectedCount)
            throw new InvalidDataException($"{path} must contain exactly {expectedCount} item.");
    }

    private static void RejectUnknownProperties(
        JsonElement element,
        string path,
        params string[] allowed)
    {
        var names = new HashSet<string>(allowed, StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject())
        {
            if (!names.Contains(property.Name))
                throw new InvalidDataException($"{path}.{property.Name} is not supported.");
        }
    }
}
