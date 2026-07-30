using System.Net.Http.Json;
using System.Text.Json;

namespace GigaClaw.ClaudeMock;

internal static class HookEmulator
{
    public static Task AcknowledgePromptAsync(Uri endpoint, string? sessionId) =>
        PostAsync(endpoint, new
        {
            session_id = sessionId ?? "mock-session",
            cwd = Environment.CurrentDirectory,
            hook_event_name = "UserPromptSubmit",
            prompt = "mock prompt acknowledgement",
        });

    public static Task InvokePreToolUseAsync(
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

    private static async Task PostAsync(Uri endpoint, object payload)
    {
        try
        {
            using var client = new HttpClient
            {
                Timeout = TimeSpan.FromSeconds(5),
            };
            using var response = await client.PostAsJsonAsync(endpoint, payload);
            var body = await response.Content.ReadAsStringAsync();
            if (!response.IsSuccessStatusCode ||
                !string.Equals(body, "{}", StringComparison.Ordinal))
            {
                await Console.Error.WriteLineAsync(
                    $"mock-claude: policy HTTP hook error {(int)response.StatusCode} " +
                    $"body '{body}' (continuing fail-open)");
            }
        }
        catch (Exception ex) when (
            ex is HttpRequestException or
            TaskCanceledException or
            IOException)
        {
            await Console.Error.WriteLineAsync(
                $"mock-claude: policy HTTP hook transport error '{ex.Message}' " +
                "(continuing fail-open)");
        }
    }
}
