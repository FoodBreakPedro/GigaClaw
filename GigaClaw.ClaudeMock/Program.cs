using GigaClaw.ClaudeMock;
using GigaClaw.Core.Automation.Policy;

// Mock `claude` CLI: parses (and ignores) the flags GigaClaw sends, drains stdin,
// picks a scenario, replays its NDJSON on stdout. Exits with the scenario's code.
//
// Selection order:
//   1. GIGACLAW_MOCK_SCENARIO env var (explicit override, used by tests)
//   2. Marker in stdin prompt: <!--scenario:NAME-->
//   3. Match by CLAUDE_AGENT env var → scenario file with same name
//   4. "default"

var sessionId = ArgParser.Get(args, "--session-id");
_ = ArgParser.Get(args, "--model");
var settingsPath = ArgParser.Get(args, "--settings");

Uri? policyHookEndpoint = null;
if (settingsPath is not null)
{
    try
    {
        policyHookEndpoint = await ClaudeHookSettings.ReadAndValidateAsync(settingsPath);
    }
    catch (Exception ex)
    {
        await Console.Error.WriteLineAsync(
            $"mock-claude: invalid --settings '{settingsPath}': {ex.Message}");
        return 2;
    }

    var ignorePolicySettings = string.Equals(
        Environment.GetEnvironmentVariable("GIGACLAW_MOCK_IGNORE_POLICY_SETTINGS"),
        "1",
        StringComparison.Ordinal);
    var acknowledgeOnceFile =
        Environment.GetEnvironmentVariable("GIGACLAW_MOCK_ACKNOWLEDGE_POLICY_ONCE_FILE");
    if (!ignorePolicySettings && !string.IsNullOrWhiteSpace(acknowledgeOnceFile))
    {
        try
        {
            using var marker = new FileStream(
                acknowledgeOnceFile,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None);
        }
        catch (IOException) when (File.Exists(acknowledgeOnceFile))
        {
            ignorePolicySettings = true;
        }
    }

    if (!ignorePolicySettings)
    {
        try
        {
            await HookEmulator.AcknowledgePromptAsync(policyHookEndpoint, sessionId);
        }
        catch (Exception ex)
        {
            await Console.Error.WriteLineAsync(
                $"mock-claude: policy hook error (continuing fail-open): {ex.Message}");
        }
    }
}
else if (string.Equals(
             Environment.GetEnvironmentVariable("GIGACLAW_MOCK_REQUIRE_POLICY_SETTINGS"),
             "1",
             StringComparison.Ordinal))
{
    await Console.Error.WriteLineAsync("mock-claude: required --settings was not provided");
    return 2;
}

var prompt = await Console.In.ReadToEndAsync();

var scenarioName =
    Environment.GetEnvironmentVariable("GIGACLAW_MOCK_SCENARIO")
    ?? ScenarioMatcher.FromPrompt(prompt)
    ?? Environment.GetEnvironmentVariable("CLAUDE_AGENT")
    ?? "default";

var loader = new ScenarioLoader(Environment.GetEnvironmentVariable("GIGACLAW_MOCK_SCENARIOS_DIR"));
var scenario = loader.Load(scenarioName) ?? loader.Load("default");
if (scenario is null)
{
    await Console.Error.WriteLineAsync($"mock-claude: no scenario named '{scenarioName}' (and no default)");
    return 2;
}

return await ScenarioReplayer.ReplayAsync(scenario, sessionId, policyHookEndpoint);
