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

    // ── C2: verdict-gated ticket exit ───────────────────────────────────────

    /// <summary>
    /// The five gating reviewers and the producing agents whose Review exit each one governs.
    /// <c>Fresh</c> records whether the reviewer's <c>inputDigest</c> is the digest of a workspace
    /// file that survives its run — the only case where re-hashing can prove a verdict is not
    /// stale. qa-tester judges a multi-file code change plus an executed test run, and the AD-7
    /// pipeline judges the ticket description, so neither can be re-hashed.
    /// </summary>
    public static TheoryData<string, string, string, bool> GatedPipelines() => new()
    {
        { "verdict-gate-qa-ship-to-done",       "qa-tester",             "programmer",       false },
        { "verdict-gate-ui-ship-to-done",       "ui-auditor",            "ui-designer",      true  },
        { "verdict-gate-blog-ship-to-seo",      "blog-reviewer",         "blog-writer",      true  },
        { "verdict-gate-media-ship-to-owner",   "local-media-reviewer",  "local-image-artist", true },
        { "content-verdict-gate-ship-to-done",  "blog-reviewer",         "content-writer",   false },
    };

    private static IEnumerable<GigaClaw.Core.Automation.Automation> VerdictGates(AutomationConfig config)
        => config.Automations.Where(a => a.Conditions.OfType<VerdictIsConditionSpec>().Any());

    private static bool Blocks(GigaClaw.Core.Automation.Automation automation)
        => automation.Actions.OfType<MoveTicketStatusActionSpec>().Any(m => m.To == "Blocked");

    [Theory]
    [MemberData(nameof(GatedPipelines))]
    public void Every_gating_reviewer_has_a_ship_arm_bound_to_its_own_verdict(
        string shipAutomationId, string reviewer, string producer, bool fresh)
    {
        var config = LoadConfig();
        var ship = Assert.Single(config.Automations, a => a.Id == shipAutomationId);

        var trigger = Assert.IsType<TicketCommentAddedTriggerSpec>(ship.Trigger);
        Assert.Contains(reviewer, trigger.Authors);

        var assignees = Assert.Single(ship.Conditions.OfType<AssignedToConditionSpec>());
        Assert.Contains(producer, assignees.Slugs);

        var column = Assert.Single(ship.Conditions.OfType<TicketInColumnConditionSpec>());
        Assert.Equal(["Review"], column.Columns);

        var verdict = Assert.Single(ship.Conditions.OfType<VerdictIsConditionSpec>());
        Assert.Equal(["SHIP"], verdict.Verdicts);
        Assert.Equal(reviewer, verdict.Agent);
        Assert.Equal(fresh, verdict.RequireFreshArtifact);

        // A SHIP arm advances; it must never be the automation that blocks.
        Assert.False(Blocks(ship));
        Assert.Contains(
            ship.Actions,
            action => action is MoveTicketStatusActionSpec or AssignTicketActionSpec or SetLabelsActionSpec);
    }

    /// <summary>
    /// The explicit C2 criterion: a reviewer that produces prose and no parseable verdict must fail
    /// loudly. Every reviewer author covered by a SHIP arm must also be covered by a gate that
    /// treats MISSING (plus INVALID and STALE) as a block.
    /// </summary>
    [Theory]
    [MemberData(nameof(GatedPipelines))]
    public void A_prose_only_review_blocks_the_ticket_instead_of_passing_it(
        string shipAutomationId, string reviewer, string producer, bool fresh)
    {
        var config = LoadConfig();

        var escalation = Assert.Single(
            VerdictGates(config),
            a => a.Trigger is TicketCommentAddedTriggerSpec t && t.Authors.Contains(reviewer)
                && a.Conditions.OfType<AssignedToConditionSpec>().Any(c => c.Slugs.Contains(producer))
                && a.Conditions.OfType<VerdictIsConditionSpec>()
                    .Any(c => c.Verdicts.Contains("MISSING", StringComparer.Ordinal)));

        // The loud-fail arm is a separate automation from the one that advances the ticket, so no
        // single condition list can be edited into passing prose through.
        Assert.NotEqual(shipAutomationId, escalation.Id);

        var verdict = Assert.Single(escalation.Conditions.OfType<VerdictIsConditionSpec>());
        foreach (var outcome in new[] { "BLOCK", "INVALID", "STALE", "MISSING" })
            Assert.Contains(outcome, verdict.Verdicts);
        Assert.DoesNotContain("SHIP", verdict.Verdicts);
        Assert.Equal(fresh, verdict.RequireFreshArtifact);
        Assert.True(Blocks(escalation), $"'{escalation.Id}' must move the ticket to Blocked.");
    }

    /// <summary>
    /// Fail closed with a paper trail: no verdict-gated automation may park a ticket in Blocked
    /// without first writing the reason onto the ticket.
    /// </summary>
    [Fact]
    public void Every_verdict_gated_block_writes_its_receipt_before_blocking()
    {
        var config = LoadConfig();
        var blocking = VerdictGates(config).Where(Blocks).ToList();
        Assert.NotEmpty(blocking);

        foreach (var automation in blocking)
        {
            var move = automation.Actions.FindIndex(
                a => a is MoveTicketStatusActionSpec { To: "Blocked" });
            var receipt = automation.Actions
                .Take(move)
                .OfType<AddCommentActionSpec>()
                .FirstOrDefault(c => !string.IsNullOrWhiteSpace(c.Content));

            Assert.True(
                receipt is not null,
                $"'{automation.Id}' blocks the ticket without a receipt comment ahead of the move.");
            Assert.Equal("automation", receipt!.Author);
        }
    }

    /// <summary>
    /// A FIX is a work order, not a rejection: every repair arm must have an escalating twin, or a
    /// ticket could round forever. The twin is identified by carrying the same reviewer authors.
    /// </summary>
    [Fact]
    public void Every_repair_arm_has_an_escalating_twin_that_carries_the_verdict_history()
    {
        var config = LoadConfig();
        var repairs = config.Automations
            .Where(a => a.Conditions.OfType<RepairBudgetConditionSpec>().Any(c => c.Mode == "withinCap"))
            .ToList();
        Assert.NotEmpty(repairs);

        foreach (var repair in repairs)
        {
            var authors = ((TicketCommentAddedTriggerSpec)repair.Trigger).Authors;

            // The repair round returns the work to the producing agent, not to some fixed slug.
            var run = Assert.Single(repair.Actions.OfType<RunAgentActionSpec>());
            Assert.Equal("{assignee}", run.Agent);
            Assert.Contains(repair.Actions.OfType<MoveTicketStatusActionSpec>(), m => m.To == "InProgress");
            Assert.Single(repair.Conditions.OfType<VerdictIsConditionSpec>(), c => c.Verdicts.SequenceEqual(["FIX"]));

            var twin = Assert.Single(
                config.Automations,
                a => a.Id != repair.Id
                    && a.Trigger is TicketCommentAddedTriggerSpec t && t.Authors.SequenceEqual(authors)
                    && a.Conditions.OfType<RepairBudgetConditionSpec>().Any(c => c.Mode == "exhausted"));

            Assert.Equal(repair.Enabled, twin.Enabled);
            Assert.True(Blocks(twin), $"'{twin.Id}' must escalate to Blocked.");
            Assert.Contains(
                twin.Actions.OfType<AddCommentActionSpec>(),
                c => c.Content.Contains("{verdictHistory}", StringComparison.Ordinal));
            Assert.DoesNotContain(twin.Actions, a => a is RunAgentActionSpec);
        }
    }

    /// <summary>
    /// The gate governs exit from Review. Acting on a ticket the reviewer already routed would
    /// re-litigate a decision that has moved on, so every arm is scoped to the Review column.
    /// </summary>
    [Fact]
    public void Verdict_gates_only_act_while_the_ticket_is_still_in_review()
    {
        var config = LoadConfig();
        foreach (var automation in VerdictGates(config))
        {
            var column = Assert.Single(automation.Conditions.OfType<TicketInColumnConditionSpec>());
            Assert.Equal(["Review"], column.Columns);
            Assert.False(column.Negate);
        }
    }

    /// <summary>
    /// A SHIP verdict is a quality judgement, not an approval to publish. Any gate that closes a
    /// ticket outright must stand down for the labels the human approval gate owns, or the
    /// verdict would silently bypass `approval-gate-on-review`.
    /// </summary>
    [Fact]
    public void A_ship_verdict_never_closes_a_ticket_that_the_owner_still_has_to_approve()
    {
        var config = LoadConfig();
        var closers = VerdictGates(config)
            .Where(a => a.Actions.OfType<MoveTicketStatusActionSpec>().Any(m => m.To == "Done"))
            .ToList();
        Assert.NotEmpty(closers);

        foreach (var automation in closers)
        {
            var guard = Assert.Single(
                automation.Conditions.OfType<LabelsConditionSpec>(), c => c.Negate);
            foreach (var label in new[] { "external", "publish", "deploy", "outbound" })
                Assert.Contains(label, guard.Labels);
        }
    }

    /// <summary>
    /// The evaluator is scored the same way but it is not a gate: `evaluator-on-done` runs after a
    /// ticket has already left the board, so gating exit on its verdict would mean re-opening
    /// finished work. Recorded as a test so a later edit has to argue with it.
    /// </summary>
    [Fact]
    public void The_evaluator_is_an_observer_and_never_gates_a_ticket_exit()
    {
        var config = LoadConfig();
        var evaluator = Assert.Single(config.Automations, a => a.Id == "evaluator-on-done");
        Assert.Empty(evaluator.Conditions.OfType<VerdictIsConditionSpec>());
        Assert.Empty(evaluator.Conditions.OfType<RepairBudgetConditionSpec>());

        foreach (var gate in VerdictGates(config))
        {
            Assert.DoesNotContain(
                gate.Conditions.OfType<VerdictIsConditionSpec>(),
                c => c.Agent == "evaluator");
            Assert.DoesNotContain(
                ((TicketCommentAddedTriggerSpec)gate.Trigger).Authors,
                author => author == "evaluator");
        }
    }

    /// <summary>
    /// The AD-7 arms ship switched off on purpose: blog-reviewer's AD-7 protocol still answers with
    /// `CONTENT-REVIEW` markers and no typed verdict, so enabling them would read as MISSING and
    /// block every content-pipeline draft. This test pins the reason to the flag.
    /// </summary>
    [Fact]
    public void The_ad7_arms_stay_off_until_the_ad7_protocol_emits_a_typed_verdict()
    {
        var config = LoadConfig();
        var ad7 = config.Automations.Where(a => a.Id.StartsWith("content-verdict-gate-", StringComparison.Ordinal)).ToList();
        Assert.Equal(4, ad7.Count);

        foreach (var automation in ad7)
        {
            Assert.False(automation.Enabled, $"'{automation.Id}' must stay disabled.");
            Assert.Contains("AD-7", automation.Name!, StringComparison.Ordinal);
            // The freshness decision is the point of the arm, so it must survive being switched on.
            Assert.All(
                automation.Conditions.OfType<VerdictIsConditionSpec>(),
                c => Assert.False(c.RequireFreshArtifact));
        }
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
