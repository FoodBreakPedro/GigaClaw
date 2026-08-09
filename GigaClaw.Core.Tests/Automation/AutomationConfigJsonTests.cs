using System.Text.Json;

namespace GigaClaw.Core.Tests.Automation;

public class AutomationConfigJsonTests
{
    private static readonly JsonSerializerOptions Opts = new() { WriteIndented = false };

    [Fact]
    public void RunAgentActionSpec_round_trip()
    {
        var spec = new RunAgentActionSpec { Agent = "programmer", MaxTurns = 50, ConcurrencyGroup = "code", LockTimeoutMinutes = 20, FallbackModel = "gpt-5.4-mini" };
        ActionSpec roundtrip = JsonSerializer.Deserialize<ActionSpec>(JsonSerializer.Serialize<ActionSpec>(spec, Opts), Opts)!;
        var a = Assert.IsType<RunAgentActionSpec>(roundtrip);
        Assert.Equal("programmer", a.Agent);
        Assert.Equal(50, a.MaxTurns);
        Assert.Equal("code", a.ConcurrencyGroup);
        Assert.Equal(20, a.LockTimeoutMinutes);
        Assert.Equal("gpt-5.4-mini", a.FallbackModel);
    }

    [Fact]
    public void RunAgentActionSpec_lockTimeout_defaults_to_null()
    {
        var spec = new RunAgentActionSpec { Agent = "programmer" };
        ActionSpec r = JsonSerializer.Deserialize<ActionSpec>(JsonSerializer.Serialize<ActionSpec>(spec, Opts), Opts)!;
        Assert.Null(Assert.IsType<RunAgentActionSpec>(r).LockTimeoutMinutes);
    }

    [Fact]
    public void MoveTicketStatusActionSpec_round_trip()
    {
        var spec = new MoveTicketStatusActionSpec { To = "InProgress" };
        ActionSpec r = JsonSerializer.Deserialize<ActionSpec>(JsonSerializer.Serialize<ActionSpec>(spec, Opts), Opts)!;
        Assert.Equal("InProgress", Assert.IsType<MoveTicketStatusActionSpec>(r).To);
    }

    [Fact]
    public void AllSubTicketsInStatusConditionSpec_round_trip()
    {
        var spec = new AllSubTicketsInStatusConditionSpec { Statuses = new() { "Done", "Review" } };
        ConditionSpec r = JsonSerializer.Deserialize<ConditionSpec>(JsonSerializer.Serialize<ConditionSpec>(spec, Opts), Opts)!;
        var c = Assert.IsType<AllSubTicketsInStatusConditionSpec>(r);
        Assert.Equal(new[] { "Done", "Review" }, c.Statuses);
    }

    [Fact]
    public void TicketCountInColumnConditionSpec_round_trip()
    {
        var spec = new TicketCountInColumnConditionSpec
        {
            Columns = new() { "InProgress" },
            SameAssignee = true,
            Operator = "==",
            Value = 0
        };
        ConditionSpec r = JsonSerializer.Deserialize<ConditionSpec>(JsonSerializer.Serialize<ConditionSpec>(spec, Opts), Opts)!;
        var c = Assert.IsType<TicketCountInColumnConditionSpec>(r);
        Assert.True(c.SameAssignee);
        Assert.Equal("==", c.Operator);
        Assert.Equal(0, c.Value);
    }

    [Fact]
    public void StatusChangeTriggerSpec_round_trip()
    {
        var spec = new StatusChangeTriggerSpec { From = "Review", To = "Done", PollSeconds = 15 };
        TriggerSpec r = JsonSerializer.Deserialize<TriggerSpec>(JsonSerializer.Serialize<TriggerSpec>(spec, Opts), Opts)!;
        var t = Assert.IsType<StatusChangeTriggerSpec>(r);
        Assert.Equal("Review", t.From);
        Assert.Equal("Done", t.To);
        Assert.Equal(15, t.PollSeconds);
    }

    [Fact]
    public void TicketInColumnTriggerSpec_round_trip()
    {
        var spec = new TicketInColumnTriggerSpec
        {
            Columns = new() { "Todo" },
            AssigneeSlug = "programmer",
            Seconds = 30,
            DebounceSeconds = 5,
            MaxConsecutiveFirings = 4,
            RetryBackoffSeconds = 90,
            ExhaustedStatus = "Blocked",
            ExhaustedComment = "Retry cap reached.",
        };
        TriggerSpec r = JsonSerializer.Deserialize<TriggerSpec>(JsonSerializer.Serialize<TriggerSpec>(spec, Opts), Opts)!;
        var t = Assert.IsType<TicketInColumnTriggerSpec>(r);
        Assert.Equal(new[] { "Todo" }, t.Columns);
        Assert.Equal("programmer", t.AssigneeSlug);
        Assert.Equal(30, t.Seconds);
        Assert.Equal(5, t.DebounceSeconds);
        Assert.Equal(4, t.MaxConsecutiveFirings);
        Assert.Equal(90, t.RetryBackoffSeconds);
        Assert.Equal("Blocked", t.ExhaustedStatus);
        Assert.Equal("Retry cap reached.", t.ExhaustedComment);
    }

    [Fact]
    public void TicketInColumnTriggerSpec_old_json_gets_safe_retry_defaults()
    {
        const string json = """{"type":"ticketInColumn","seconds":30,"columns":["Todo"]}""";
        var trigger = Assert.IsType<TicketInColumnTriggerSpec>(
            JsonSerializer.Deserialize<TriggerSpec>(json, Opts));

        Assert.Equal(3, trigger.MaxConsecutiveFirings);
        Assert.Equal(30, trigger.RetryBackoffSeconds);
    }

    [Fact]
    public void HttpRequestActionSpec_round_trip()
    {
        var spec = new HttpRequestActionSpec
        {
            Url = "https://cms.example/api/publish",
            Method = "PUT",
            Headers = new() { ["X-Source"] = "gigaclaw" },
            BodyTemplate = """{"ticket":"{ticketId}"}""",
            TimeoutSeconds = 12,
            AbortOnFailure = true,
            SecretRef = "CMS_API_TOKEN",
            FailureComment = "Dispatch failed: {http.status} {http.error}",
            FailureStatus = "Blocked",
        };
        ActionSpec r = JsonSerializer.Deserialize<ActionSpec>(JsonSerializer.Serialize<ActionSpec>(spec, Opts), Opts)!;
        var a = Assert.IsType<HttpRequestActionSpec>(r);
        Assert.Equal("https://cms.example/api/publish", a.Url);
        Assert.Equal("PUT", a.Method);
        Assert.Equal("gigaclaw", a.Headers["X-Source"]);
        Assert.Equal("""{"ticket":"{ticketId}"}""", a.BodyTemplate);
        Assert.Equal(12, a.TimeoutSeconds);
        Assert.True(a.AbortOnFailure);
        Assert.Equal("CMS_API_TOKEN", a.SecretRef);
        Assert.Equal("Dispatch failed: {http.status} {http.error}", a.FailureComment);
        Assert.Equal("Blocked", a.FailureStatus);
    }

    [Fact]
    public void HttpRequestActionSpec_minimal_json_gets_safe_defaults()
    {
        const string json = """{"type":"httpRequest","url":"https://example.com/hook"}""";
        var spec = Assert.IsType<HttpRequestActionSpec>(JsonSerializer.Deserialize<ActionSpec>(json, Opts));

        Assert.Equal("POST", spec.Method);
        Assert.Equal(30, spec.TimeoutSeconds);
        Assert.False(spec.AbortOnFailure);
        Assert.Null(spec.SecretRef);
        Assert.Empty(spec.Headers);
        Assert.Equal("", spec.BodyTemplate);
        Assert.Null(spec.FailureComment);
        Assert.Null(spec.FailureStatus);
    }

    [Fact]
    public void HttpRequestActionSpec_serializes_with_the_httpRequest_discriminator()
    {
        var json = JsonSerializer.Serialize<ActionSpec>(new HttpRequestActionSpec { Url = "https://x/y" }, Opts);
        Assert.Contains("\"type\":\"httpRequest\"", json);
        // The secret itself must never reach automations.json — only the variable name may.
        Assert.DoesNotContain("Bearer", json);
    }

    /// <summary>
    /// Plan 2.1's cap is JSON-only for now (no settings UI), so automations.json is its entire
    /// surface — it has to read through <see cref="AutomationStore.JsonOptions"/> under the exact
    /// camelCase spelling the documentation gives owners.
    /// </summary>
    [Fact]
    public void AutomationConfig_reads_maxTicketCostUsd_from_automations_json()
    {
        const string json = """{"dailyBudgetUsd":40,"maxTicketCostUsd":5.5,"automations":[]}""";
        var config = JsonSerializer.Deserialize<AutomationConfig>(json, AutomationStore.JsonOptions)!;

        Assert.Equal(40m, config.DailyBudgetUsd);
        Assert.Equal(5.5m, config.MaxTicketCostUsd);
    }

    [Fact]
    public void AutomationConfig_without_maxTicketCostUsd_leaves_the_cap_disabled()
    {
        var config = JsonSerializer.Deserialize<AutomationConfig>(
            """{"automations":[]}""", AutomationStore.JsonOptions)!;

        Assert.Null(config.MaxTicketCostUsd);
    }

    [Fact]
    public void Condition_Negate_round_trip()
    {
        var spec = new TicketInColumnConditionSpec { Columns = new() { "Todo" }, Negate = true };
        ConditionSpec r = JsonSerializer.Deserialize<ConditionSpec>(JsonSerializer.Serialize<ConditionSpec>(spec, Opts), Opts)!;
        Assert.True(r.Negate);
    }
}
