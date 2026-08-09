using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

var prompt = await Console.In.ReadToEndAsync();
var scenario = Environment.GetEnvironmentVariable("GIGACLAW_CODEX_MOCK_SCENARIO") ?? "default";
var sessionId = ResolveSessionId(args) ?? "019fd288-363e-7093-92f5-cba942f8eb57";
var model = ResolveOption(args, "--model");

var endpoint = args
    .Select(arg => Regex.Match(arg, @"http://127\.0\.0\.1:\d+/policy/[a-f0-9]+"))
    .FirstOrDefault(match => match.Success)
    ?.Value;
if (endpoint is not null)
{
    using var client = new HttpClient();
    var acknowledgement = JsonSerializer.Serialize(new
    {
        session_id = sessionId,
        transcript_path = (string?)null,
        cwd = Environment.CurrentDirectory,
        hook_event_name = "UserPromptSubmit",
        model = "mock-codex",
        permission_mode = "dontAsk",
        prompt,
    });
    using var content = new StringContent(acknowledgement, Encoding.UTF8, "application/json");
    await client.PostAsync(endpoint, content);
}

Write(new { type = "thread.started", thread_id = sessionId });
Write(new { type = "turn.started" });

if (scenario == "error-exit"
    || (scenario == "primary-model-fails" && model != "gpt-5.4-mini"))
{
    Write(new { type = "turn.failed", error = new { message = "mock Codex failure" } });
    return 1;
}

Write(new
{
    type = "item.completed",
    item = new { id = "item_0", type = "agent_message", text = "CODEX_MOCK_OK" },
});
Write(new
{
    type = "turn.completed",
    usage = new
    {
        input_tokens = 14528,
        cached_input_tokens = 9984,
        cache_write_input_tokens = 0,
        output_tokens = 10,
        reasoning_output_tokens = 0,
    },
});

if (scenario == "linger-after-success")
{
    await Task.Delay(TimeSpan.FromMinutes(5));
}

return 0;

static string? ResolveSessionId(string[] args)
{
    var resume = Array.IndexOf(args, "resume");
    return resume >= 0 && resume + 1 < args.Length ? args[resume + 1] : null;
}

static string? ResolveOption(string[] args, string option)
{
    var index = Array.IndexOf(args, option);
    return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
}

static void Write(object value) => Console.WriteLine(JsonSerializer.Serialize(value));
