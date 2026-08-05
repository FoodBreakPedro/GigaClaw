using System.Diagnostics;
using System.Text.Json;

namespace GigaClaw.Core.Automation;

internal sealed class CodexStreamState
{
    public int AssistantEventCount;
    public int TerminalOutcome;
}

internal static class CodexStreamPump
{
    internal static async Task PumpStdoutAsync(
        Process proc,
        AgentRun run,
        CodexStreamState state,
        Action<string> onThreadStarted,
        CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                string? line;
                try { line = await proc.StandardOutput.ReadLineAsync(ct); }
                catch (OperationCanceledException) { break; }
                if (line is null) break;
                if (string.IsNullOrWhiteSpace(line)) continue;
                ParseLine(line, run, state, onThreadStarted);
            }
        }
        catch (Exception ex)
        {
            try { run.Push(new StreamEvent(DateTime.UtcNow, "error", $"Codex stdout pump failed: {ex.Message}")); }
            catch { }
        }
    }

    internal static void ParseLine(
        string line,
        AgentRun run,
        CodexStreamState state,
        Action<string> onThreadStarted)
    {
        try
        {
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            var type = ReadString(root, "type") ?? "event";

            switch (type)
            {
                case "thread.started":
                    var threadId = ReadString(root, "thread_id");
                    if (!string.IsNullOrWhiteSpace(threadId)) onThreadStarted(threadId);
                    run.Push(new StreamEvent(DateTime.UtcNow, "system", "Codex thread started", line));
                    return;

                case "turn.started":
                    run.Push(new StreamEvent(DateTime.UtcNow, "system", "Codex turn started", line));
                    return;

                case "turn.completed":
                    RecordUsage(root, run);
                    Interlocked.CompareExchange(ref state.TerminalOutcome, 1, 0);
                    run.Push(new StreamEvent(DateTime.UtcNow, "result", "Codex turn completed", line));
                    return;

                case "turn.failed":
                    RecordUsage(root, run);
                    Interlocked.Exchange(ref state.TerminalOutcome, -1);
                    run.Push(new StreamEvent(DateTime.UtcNow, "error", ExtractError(root), line));
                    return;

                case "error":
                    run.Push(new StreamEvent(DateTime.UtcNow, "error", ExtractError(root), line));
                    return;
            }

            if (!type.StartsWith("item.", StringComparison.Ordinal) ||
                !root.TryGetProperty("item", out var item) ||
                item.ValueKind != JsonValueKind.Object)
            {
                run.Push(new StreamEvent(DateTime.UtcNow, type, Flatten(root), line));
                return;
            }

            var itemType = ReadString(item, "type") ?? "item";
            if (itemType == "agent_message" && type == "item.completed")
            {
                Interlocked.Increment(ref state.AssistantEventCount);
                run.Push(new StreamEvent(DateTime.UtcNow, "assistant", ReadString(item, "text") ?? "", line));
            }
            else if (itemType == "reasoning" && type == "item.completed")
            {
                run.Push(new StreamEvent(DateTime.UtcNow, "reasoning", ReadString(item, "text") ?? Flatten(item), line));
            }
            else if (itemType == "command_execution" && type == "item.started")
            {
                run.Push(new StreamEvent(DateTime.UtcNow, "tool_use", ReadString(item, "command") ?? "command", line));
            }
            else if (type == "item.completed")
            {
                run.Push(new StreamEvent(DateTime.UtcNow, "tool_result", DescribeItem(itemType, item), line));
            }
        }
        catch (JsonException)
        {
            run.Push(new StreamEvent(DateTime.UtcNow, "raw", line));
        }
    }

    internal static async Task PumpStderrAsync(Process proc, AgentRun run, CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                string? line;
                try { line = await proc.StandardError.ReadLineAsync(ct); }
                catch (OperationCanceledException) { break; }
                if (line is null) break;
                if (!string.IsNullOrWhiteSpace(line))
                    run.Push(new StreamEvent(DateTime.UtcNow, "stderr", line));
            }
        }
        catch (Exception ex)
        {
            try { run.Push(new StreamEvent(DateTime.UtcNow, "error", $"Codex stderr pump failed: {ex.Message}")); }
            catch { }
        }
    }

    internal static async Task PumpSteeringAsync(AgentRun run, CancellationToken ct)
    {
        try
        {
            while (await run.SteeringQueue.Reader.WaitToReadAsync(ct))
            {
                while (run.SteeringQueue.Reader.TryRead(out var message))
                {
                    run.Push(new StreamEvent(DateTime.UtcNow, "steer", message));
                    run.AddPendingSteerMessage(message);
                }
            }
        }
        catch (OperationCanceledException) { }
    }

    private static void RecordUsage(JsonElement root, AgentRun run)
    {
        if (!root.TryGetProperty("usage", out var usage) || usage.ValueKind != JsonValueKind.Object)
            return;

        run.AddUsage(
            ReadInt(usage, "input_tokens"),
            ReadInt(usage, "output_tokens"),
            ReadInt(usage, "cached_input_tokens"),
            ReadInt(usage, "cache_write_input_tokens"),
            costUsd: null);
    }

    private static string DescribeItem(string itemType, JsonElement item) => itemType switch
    {
        "command_execution" => ReadString(item, "aggregated_output") ?? ReadString(item, "command") ?? itemType,
        "file_change" => "File changes completed",
        "mcp_tool_call" => ReadString(item, "tool") ?? itemType,
        "web_search" => ReadString(item, "query") ?? itemType,
        _ => Flatten(item),
    };

    private static string ExtractError(JsonElement root)
    {
        if (root.TryGetProperty("error", out var error))
        {
            if (error.ValueKind == JsonValueKind.String) return error.GetString() ?? "Codex turn failed";
            if (error.ValueKind == JsonValueKind.Object)
                return ReadString(error, "message") ?? Flatten(error);
        }
        return ReadString(root, "message") ?? "Codex turn failed";
    }

    private static string? ReadString(JsonElement root, string property) =>
        root.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static int ReadInt(JsonElement root, string property) =>
        root.TryGetProperty(property, out var value) && value.TryGetInt32(out var parsed) ? parsed : 0;

    private static string Flatten(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => value.GetString() ?? "",
        JsonValueKind.Object => string.Join(" ", value.EnumerateObject().Select(p => Flatten(p.Value)).Where(v => v.Length > 0)),
        JsonValueKind.Array => string.Join(" ", value.EnumerateArray().Select(Flatten).Where(v => v.Length > 0)),
        JsonValueKind.Null => "",
        _ => value.ToString(),
    };
}
