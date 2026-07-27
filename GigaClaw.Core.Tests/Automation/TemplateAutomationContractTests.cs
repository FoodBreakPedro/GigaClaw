using System.Text.Json;
using GigaClaw.Core.Automation;

namespace GigaClaw.Core.Tests.Automation;

/// <summary>
/// Guards the cross-agent orchestration contract shipped in ProjectTemplate. These
/// assertions intentionally deserialize through the production options so template
/// drift is caught before a new workspace receives it.
/// </summary>
public class TemplateAutomationContractTests
{
    private static readonly string AgentsDir = Path.Combine(FindRepositoryRoot(), "ProjectTemplate", "Agents");

    private static AutomationConfig LoadConfig()
    {
        var json = File.ReadAllText(Path.Combine(AgentsDir, "automations.json"));
        return JsonSerializer.Deserialize<AutomationConfig>(json, AutomationStore.JsonOptions)
            ?? throw new InvalidDataException("Template automations.json deserialized to null.");
    }

    [Fact]
    public void Template_deserializes_with_unique_ids_and_resolvable_agent_slugs()
    {
        var config = LoadConfig();
        Assert.NotEmpty(config.Automations);
        Assert.Equal(
            config.Automations.Count,
            config.Automations.Select(a => a.Id).Distinct(StringComparer.Ordinal).Count());

        foreach (var automation in config.Automations)
        {
            foreach (var run in automation.Actions.OfType<RunAgentActionSpec>())
            {
                if (!run.Agent.Contains("{assignee}", StringComparison.Ordinal))
                {
                    Assert.True(
                        File.Exists(Path.Combine(AgentsDir, run.Agent, "SKILL.md")),
                        $"Automation '{automation.Id}' references missing agent '{run.Agent}'.");
                    continue;
                }

                var assigneeCondition = Assert.Single(
                    automation.Conditions.OfType<AssignedToConditionSpec>());
                foreach (var slug in assigneeCondition.Slugs)
                {
                    Assert.True(
                        File.Exists(Path.Combine(AgentsDir, slug, "SKILL.md")),
                        $"Automation '{automation.Id}' can resolve {{assignee}} to missing agent '{slug}'.");
                }
            }
        }
    }

    [Fact]
    public void Shared_contract_manifest_covers_every_template_agent()
    {
        using var document = JsonDocument.Parse(
            File.ReadAllText(Path.Combine(AgentsDir, "contracts.json")));
        var root = document.RootElement;
        Assert.Equal(JsonValueKind.Object, root.GetProperty("defaults").ValueKind);
        var contracts = root.GetProperty("agents");

        foreach (var skill in Directory.EnumerateFiles(
            AgentsDir, "SKILL.md", SearchOption.AllDirectories))
        {
            var slug = new DirectoryInfo(Path.GetDirectoryName(skill)!).Name;
            Assert.True(
                contracts.TryGetProperty(slug, out var contract)
                && contract.ValueKind == JsonValueKind.Object,
                $"Shared contract manifest is missing template agent '{slug}'.");
        }
    }

    [Fact]
    public void Done_commit_and_decision_receipt_are_serialized_by_contract()
    {
        var config = LoadConfig();
        var committer = Assert.Single(config.Automations, a => a.Id == "committer-on-done");
        var exclusion = Assert.Single(committer.Conditions.OfType<LabelsConditionSpec>());
        Assert.True(exclusion.Negate);
        Assert.Contains("decision", exclusion.Labels);
        Assert.Contains("adr", exclusion.Labels);

        var decision = Assert.Single(config.Automations, a => a.Id == "decision-receipt-on-done");
        var decisionRun = Assert.Single(decision.Actions.OfType<RunAgentActionSpec>());
        Assert.Equal("decision-engine", decisionRun.Agent);
        Assert.Equal("git", decisionRun.ConcurrencyGroup);
    }

    [Fact]
    public void Approval_lifecycle_has_invalidation_and_marking_automations()
    {
        var config = LoadConfig();
        var invalidate = Assert.Single(config.Automations, a => a.Id == "approval-clear-on-revision");
        var invalidateLabels = Assert.Single(invalidate.Actions.OfType<SetLabelsActionSpec>());
        Assert.Contains("pending-approval", invalidateLabels.Remove);
        Assert.Contains("approved", invalidateLabels.Remove);

        var mark = Assert.Single(config.Automations, a => a.Id == "approval-mark-on-done");
        var markLabels = Assert.Single(mark.Actions.OfType<SetLabelsActionSpec>());
        Assert.Contains("approved", markLabels.Add);
        Assert.Contains("pending-approval", markLabels.Remove);
    }

    [Fact]
    public void Resume_dispatch_is_durably_bounded()
    {
        var config = LoadConfig();
        var resume = Assert.Single(config.Automations, a => a.Id == "assignee-resume");
        var trigger = Assert.IsType<TicketInColumnTriggerSpec>(resume.Trigger);
        using var contracts = JsonDocument.Parse(
            File.ReadAllText(Path.Combine(AgentsDir, "contracts.json")));
        var defaults = contracts.RootElement.GetProperty("defaults");

        Assert.Equal(
            defaults.GetProperty("maxDispatchAttempts").GetInt32(),
            trigger.MaxConsecutiveFirings);
        Assert.Equal(
            defaults.GetProperty("retryBackoffSeconds").GetInt32(),
            trigger.RetryBackoffSeconds);
        Assert.Equal("Blocked", trigger.ExhaustedStatus);
        Assert.False(string.IsNullOrWhiteSpace(trigger.ExhaustedComment));
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "GigaClaw.slnx")))
                return directory.FullName;
            directory = directory.Parent;
        }
        throw new DirectoryNotFoundException("Could not locate GigaClaw repository root.");
    }
}
