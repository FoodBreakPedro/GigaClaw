using System.Text.Json;

namespace GigaClaw.ClaudeMock;

internal static class ScenarioReplayer
{
    public static async Task<int> ReplayAsync(
        string[] lines,
        string? sessionId,
        Uri? policyHookEndpoint = null)
    {
        int exitCode = 0;
        // Indexed rather than foreach: a denied PreToolUse has to be able to consume the scenario
        // lines that the denied tool call would have produced (see skip_on_deny below).
        for (var lineIndex = 0; lineIndex < lines.Length; lineIndex++)
        {
            var line = lines[lineIndex].Trim();
            if (line.Length == 0) continue;

            // Look for our extension fields without disturbing real-claude-compatible JSON.
            int delayMs = 0;
            string emit = line;
            try
            {
                using var doc = JsonDocument.Parse(line);
                var root = doc.RootElement;

                // _hook line: explicitly emulate a Claude hook invocation against the
                // generated settings endpoint. It is never emitted on stdout.
                if (root.TryGetProperty("_hook", out var hook))
                {
                    if (policyHookEndpoint is null)
                        throw new InvalidOperationException(
                            "Scenario requested a hook but --settings was not provided.");
                    var denial = await HookEmulator.InvokePreToolUseAsync(
                        policyHookEndpoint,
                        sessionId,
                        hook);

                    if (denial is not null)
                    {
                        // A denied PreToolUse means the real subprocess never runs the tool, so the
                        // scenario's next `skip_on_deny` lines — the events that call would have
                        // produced — must not be emitted either. Without this the mock would report
                        // a blocked write as having happened anyway, and the block would look
                        // enforced while proving nothing.
                        await Console.Out.WriteLineAsync(JsonSerializer.Serialize(new
                        {
                            type = "policy_denied",
                            tool_name = hook.TryGetProperty("tool_name", out var t) &&
                                        t.ValueKind == JsonValueKind.String
                                ? t.GetString()
                                : null,
                            reason = denial,
                        }));

                        var skip = hook.TryGetProperty("skip_on_deny", out var skipElement) &&
                                   skipElement.ValueKind == JsonValueKind.Number &&
                                   skipElement.TryGetInt32(out var n) && n > 0
                            ? n
                            : 0;
                        for (var i = 0; i < skip && lineIndex + 1 < lines.Length; i++)
                            lineIndex++;
                    }
                    continue;
                }

                // _meta line: control envelope, do not emit on stdout
                if (root.TryGetProperty("_meta", out var meta))
                {
                    if (meta.TryGetProperty("exit", out var ex) && ex.TryGetInt32(out var code))
                        exitCode = code;
                    if (meta.TryGetProperty("delay_ms", out var d) && d.TryGetInt32(out var ms))
                        await Task.Delay(ms);
                    continue;
                }

                if (root.TryGetProperty("_delay_ms", out var dl) && dl.TryGetInt32(out var ms2))
                    delayMs = ms2;

                if (sessionId is not null && root.TryGetProperty("session_id", out var s) && s.ValueKind == JsonValueKind.String)
                {
                    // Pass-through: scenarios may use a placeholder session id; rewrite if needed.
                    if (s.GetString() == "{{session_id}}")
                        emit = line.Replace("{{session_id}}", sessionId);
                }
            }
            catch (JsonException)
            {
                // Non-JSON lines (e.g. comments) are ignored — real claude never emits these,
                // so dropping them keeps the consumer's parser happy.
                continue;
            }

            await Console.Out.WriteLineAsync(emit);
            await Console.Out.FlushAsync();
            if (delayMs > 0) await Task.Delay(delayMs);
        }
        return exitCode;
    }
}
