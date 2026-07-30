using System.Net.Http.Json;
using System.Text.Json;

namespace GigaClaw.ClaudeMock;

internal static class HookEmulator
{
    public static Task<string?> AcknowledgePromptAsync(Uri endpoint, string? sessionId) =>
        PostAsync(endpoint, new
        {
            session_id = sessionId ?? "mock-session",
            cwd = Environment.CurrentDirectory,
            hook_event_name = "UserPromptSubmit",
            prompt = "mock prompt acknowledgement",
        });

    /// <summary>
    /// Returns the denial reason when the policy hook refused the tool call, or null when it was
    /// allowed. A real Claude subprocess would not run the tool; the scenario replayer models that
    /// by skipping the events the denied call would have produced.
    /// </summary>
    public static Task<string?> InvokePreToolUseAsync(
        Uri endpoint,
        string? sessionId,
        JsonElement hook)
    {
        if (hook.ValueKind != JsonValueKind.Object ||
            !hook.TryGetProperty("tool_name", out var toolName) ||
            toolName.ValueKind != JsonValueKind.String ||
            !hook.TryGetProperty("tool_input", out var toolInput) ||
            toolInput.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException(
                "Mock _hook requires string tool_name and object tool_input.");
        }

        var toolUseId = hook.TryGetProperty("tool_use_id", out var id) &&
                        id.ValueKind == JsonValueKind.String
            ? id.GetString()
            : $"toolu_mock_{Guid.NewGuid():N}";
        return PostAsync(endpoint, new
        {
            session_id = sessionId ?? "mock-session",
            cwd = Environment.CurrentDirectory,
            hook_event_name = "PreToolUse",
            tool_name = toolName.GetString(),
            tool_input = toolInput,
            tool_use_id = toolUseId,
        });
    }

    private static async Task<string?> PostAsync(Uri endpoint, object payload)
    {
        try
        {
            using var client = new HttpClient
            {
                Timeout = TimeSpan.FromSeconds(5),
            };
            using var response = await client.PostAsJsonAsync(endpoint, payload);
            var body = await response.Content.ReadAsStringAsync();

            if (response.IsSuccessStatusCode && TryReadDenial(body, out var reason))
                return reason;

            if (!response.IsSuccessStatusCode ||
                !string.Equals(body, "{}", StringComparison.Ordinal))
            {
                await Console.Error.WriteLineAsync(
                    $"mock-claude: policy HTTP hook error {(int)response.StatusCode} " +
                    $"body '{body}' (continuing fail-open)");
            }
            return null;
        }
        catch (Exception ex) when (
            ex is HttpRequestException or
            TaskCanceledException or
            IOException)
        {
            // Transport failure still fails open here; the runner separately fails the attempt
            // when Claude never acknowledged the hook, which is what stops a silent bypass.
            await Console.Error.WriteLineAsync(
                $"mock-claude: policy HTTP hook transport error '{ex.Message}' " +
                "(continuing fail-open)");
            return null;
        }
    }

    /// <summary>Parses Claude Code's PreToolUse deny body. Anything else is not a denial.</summary>
    private static bool TryReadDenial(string body, out string? reason)
    {
        reason = null;
        if (string.IsNullOrWhiteSpace(body) || body.Length > 64 * 1024)
            return false;

        try
        {
            using var document = JsonDocument.Parse(body);
            if (document.RootElement.ValueKind != JsonValueKind.Object ||
                !document.RootElement.TryGetProperty("hookSpecificOutput", out var output) ||
                output.ValueKind != JsonValueKind.Object ||
                !output.TryGetProperty("permissionDecision", out var decision) ||
                decision.ValueKind != JsonValueKind.String ||
                !string.Equals(decision.GetString(), "deny", StringComparison.Ordinal))
            {
                return false;
            }

            reason = output.TryGetProperty("permissionDecisionReason", out var r) &&
                     r.ValueKind == JsonValueKind.String
                ? r.GetString()
                : "policy denied the tool call";
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
