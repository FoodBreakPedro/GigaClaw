using System.Text.Json;
using GigaClaw.Catalog;
using GigaClaw.Core.Automation;
using GigaClaw.Core.Automation.Verdicts;

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

        // {assignee} expansion has a single implementation — CatalogGenerator.ReadAutomations.
        // This test consumes its output instead of re-deriving the slug set from conditions.
        var expanded = CatalogGenerator.ReadAutomations(AgentsDir, "template")
            .ToDictionary(info => info.Id, StringComparer.Ordinal);

        // Round-trip: the typed model and the raw-JSON reader agree on the automation set,
        // so neither representation can silently drop an automation.
        Assert.Equal(
            config.Automations.Select(a => a.Id).Order(StringComparer.Ordinal),
            expanded.Keys.Order(StringComparer.Ordinal));

        foreach (var automation in config.Automations)
        {
            var runs = automation.Actions.OfType<RunAgentActionSpec>().ToList();
            var info = expanded[automation.Id];
            Assert.Equal(automation.Enabled, info.Enabled);

            foreach (var run in runs)
            {
                if (run.Agent == "{assignee}")
                {
                    // Structural: expansion is only well-defined against exactly one
                    // assignedTo condition, and it must never expand to nothing.
                    var assigneeCondition = Assert.Single(
                        automation.Conditions.OfType<AssignedToConditionSpec>());
                    Assert.NotEmpty(assigneeCondition.Slugs);

                    // The expansion covers every member the condition names.
                    foreach (var slug in assigneeCondition.Slugs)
                    {
                        Assert.Contains(info.Bindings, binding => binding.Agent == slug);
                    }
                }
                else
                {
                    // The placeholder appears whole or not at all: a partial "{assignee}"
                    // would be dispatched literally instead of expanded.
                    Assert.DoesNotContain("{assignee}", run.Agent, StringComparison.Ordinal);
                }
            }

            // Typed model and expansion agree on whether this automation dispatches agents.
            Assert.Equal(runs.Count > 0, info.Bindings.Count > 0);

            // Every expanded binding resolves to a real template agent.
            foreach (var binding in info.Bindings)
            {
                Assert.DoesNotContain("{assignee}", binding.Agent, StringComparison.Ordinal);
                Assert.True(
                    File.Exists(Path.Combine(AgentsDir, binding.Agent, "SKILL.md")),
                    $"Automation '{automation.Id}' dispatches missing agent '{binding.Agent}'.");
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
        // Phase 1.5 (return-to-sender): spent dispatch retries no longer park the ticket on the
        // owner. It goes back to Backlog owned by the groomer, which is the column+assignee pair
        // the `groomer` automation watches — so triage picks it up without anyone being paged.
        Assert.Equal("Backlog", trigger.ExhaustedStatus);
        Assert.Equal("groomer", trigger.ExhaustedAssignee);
        Assert.False(string.IsNullOrWhiteSpace(trigger.ExhaustedComment));
    }

    /// <summary>
    /// The loop `exhaustedAssignee` could create, pinned closed: an automation that dispatches the
    /// groomer must not also hand the groomer its own exhausted tickets, or a ticket the groomer
    /// cannot groom would be re-served to it forever. The groomer's own arm parks in Blocked
    /// instead, which is the one place the series genuinely has to stop.
    /// </summary>
    [Fact]
    public void No_automation_routes_its_own_exhaustion_back_to_the_agent_it_dispatches()
    {
        var config = LoadConfig();
        foreach (var automation in config.Automations)
        {
            if (automation.Trigger is not TicketInColumnTriggerSpec trigger) continue;
            if (string.IsNullOrWhiteSpace(trigger.ExhaustedAssignee)) continue;

            var dispatched = automation.Actions.OfType<RunAgentActionSpec>()
                .Select(run => run.Agent)
                .ToHashSet(StringComparer.Ordinal);
            Assert.DoesNotContain(
                trigger.ExhaustedAssignee!,
                dispatched);
        }

        var groomer = Assert.Single(config.Automations, a => a.Id == "groomer");
        var groomerTrigger = Assert.IsType<TicketInColumnTriggerSpec>(groomer.Trigger);
        Assert.Equal("Blocked", groomerTrigger.ExhaustedStatus);
        Assert.True(string.IsNullOrWhiteSpace(groomerTrigger.ExhaustedAssignee));
    }

    /// <summary>
    /// Phase 1.1: the intake arm is what makes "post a ticket to Backlog with no assignee" a
    /// complete request. It must filter on the trigger's own <c>unassigned</c> flag rather than on
    /// an assignee slug — an empty <c>assigneeSlug</c> means "any assignee", which would make
    /// intake and the `groomer` arm fight over the same tickets.
    /// </summary>
    [Fact]
    public void Backlog_intake_and_the_groomer_arm_filter_on_disjoint_assignee_states()
    {
        var config = LoadConfig();
        var intake = Assert.Single(config.Automations, a => a.Id == "backlog-intake");
        var groomer = Assert.Single(config.Automations, a => a.Id == "groomer");

        var intakeTrigger = Assert.IsType<TicketInColumnTriggerSpec>(intake.Trigger);
        Assert.Equal(["Backlog"], intakeTrigger.Columns);
        Assert.True(intakeTrigger.Unassigned);
        Assert.True(string.IsNullOrEmpty(intakeTrigger.AssigneeSlug));

        var groomerTrigger = Assert.IsType<TicketInColumnTriggerSpec>(groomer.Trigger);
        Assert.Equal(["Backlog"], groomerTrigger.Columns);
        Assert.Equal("groomer", groomerTrigger.AssigneeSlug);
        Assert.False(groomerTrigger.Unassigned);

        // Intake hands the ticket over; it never dispatches an agent itself, so the existing
        // `groomer` arm stays the single place a grooming run is started.
        Assert.Empty(intake.Actions.OfType<RunAgentActionSpec>());
        var assign = Assert.Single(intake.Actions.OfType<AssignTicketActionSpec>());
        Assert.Equal("groomer", assign.Slug);
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
    /// loudly. Phase 1.2 changed *when* it fails, not whether: an unusable verdict (INVALID, STALE,
    /// MISSING) is the reviewer's own output failing, so the reviewer is asked once more before the
    /// ticket blocks. What this test pins is that the retry is bounded and terminates in a block —
    /// the one arm that ends the story still names MISSING and still moves the ticket to Blocked.
    /// </summary>
    [Theory]
    [MemberData(nameof(GatedPipelines))]
    public void A_prose_only_review_blocks_the_ticket_instead_of_passing_it(
        string shipAutomationId, string reviewer, string producer, bool fresh)
    {
        var config = LoadConfig();

        var escalation = Assert.Single(
            VerdictGates(config),
            a => Blocks(a) && Governs(a, reviewer, producer)
                && a.Conditions.OfType<VerdictIsConditionSpec>()
                    .Any(c => c.Verdicts.Contains("MISSING", StringComparer.Ordinal)));

        // The loud-fail arm is a separate automation from the one that advances the ticket, so no
        // single condition list can be edited into passing prose through.
        Assert.NotEqual(shipAutomationId, escalation.Id);

        var verdict = Assert.Single(escalation.Conditions.OfType<VerdictIsConditionSpec>());
        foreach (var outcome in new[] { "INVALID", "STALE", "MISSING" })
            Assert.Contains(outcome, verdict.Verdicts);
        Assert.DoesNotContain("SHIP", verdict.Verdicts);
        Assert.Equal(fresh, verdict.RequireFreshArtifact);

        // It blocks only once the retry is spent, and there is a matching arm that spends it.
        Assert.Single(
            escalation.Conditions.OfType<ReviewerRetryBudgetConditionSpec>(),
            c => c.Mode == "exhausted");
        var retry = Assert.Single(
            VerdictGates(config),
            a => Governs(a, reviewer, producer)
                && a.Conditions.OfType<ReviewerRetryBudgetConditionSpec>().Any(c => c.Mode == "withinCap"));
        Assert.False(Blocks(retry), $"'{retry.Id}' must re-review, not block.");

        // Return-to-sender: the retry re-runs the REVIEWER, never the author — the author was not
        // the one who failed to produce a verdict.
        var rerun = Assert.Single(retry.Actions.OfType<RunAgentActionSpec>());
        Assert.Equal(reviewer, rerun.Agent);
        Assert.DoesNotContain(retry.Actions, a => a is MoveTicketStatusActionSpec);
    }

    /// <summary>
    /// A deliberate BLOCK is the one verdict outcome that never earns a second chance: the reviewer
    /// judged the work rather than failing to report on it. It escalates on the first firing, on
    /// its own arm, with no retry-budget condition that could hold it back.
    /// </summary>
    [Theory]
    [MemberData(nameof(GatedPipelines))]
    public void A_deliberate_block_verdict_escalates_immediately(
        string shipAutomationId, string reviewer, string producer, bool fresh)
    {
        var config = LoadConfig();

        var block = Assert.Single(
            VerdictGates(config),
            a => Blocks(a) && Governs(a, reviewer, producer)
                && a.Conditions.OfType<VerdictIsConditionSpec>()
                    .Any(c => c.Verdicts.Contains("BLOCK", StringComparer.Ordinal)));

        Assert.NotEqual(shipAutomationId, block.Id);
        var verdict = Assert.Single(block.Conditions.OfType<VerdictIsConditionSpec>());
        Assert.Equal(["BLOCK"], verdict.Verdicts);
        Assert.Equal(fresh, verdict.RequireFreshArtifact);
        Assert.Empty(block.Conditions.OfType<ReviewerRetryBudgetConditionSpec>());
        Assert.Empty(block.Conditions.OfType<RepairBudgetConditionSpec>());
    }

    /// <summary>
    /// Phase 1.4: a Blocked ticket has an owner. Every arm that parks a ticket in Blocked assigns
    /// it to the human first, so "Blocked" means "a person has to decide" rather than leaving the
    /// ticket nominally owned by the agent that could not finish it.
    /// </summary>
    [Fact]
    public void Every_verdict_gated_block_hands_the_ticket_to_the_owner()
    {
        var config = LoadConfig();
        var blocking = VerdictGates(config).Where(Blocks).ToList();
        Assert.NotEmpty(blocking);

        foreach (var automation in blocking)
        {
            var move = automation.Actions.FindIndex(a => a is MoveTicketStatusActionSpec { To: "Blocked" });
            var assign = automation.Actions.Take(move).OfType<AssignTicketActionSpec>().LastOrDefault();
            Assert.True(
                assign is not null,
                $"'{automation.Id}' blocks the ticket without assigning an owner first.");
            Assert.Equal("owner", assign!.Slug);
        }
    }

    private static bool Governs(GigaClaw.Core.Automation.Automation automation, string reviewer, string producer)
        => automation.Trigger is TicketCommentAddedTriggerSpec t && t.Authors.Contains(reviewer)
            && automation.Conditions.OfType<AssignedToConditionSpec>().Any(c => c.Slugs.Contains(producer));

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
    /// ticket could round forever. The twin is identified by carrying the same reviewer authors
    /// <em>and</em> the same <c>extended-repair</c> gating — Phase 1.6 duplicated every repair pair
    /// into a base arm that stands down for the label and a twin that requires it, so pairing on
    /// authors alone would now match two exhaustion arms per family.
    /// <para>
    /// Phase 1.3 also changed what exhaustion does: instead of parking the ticket on the owner it
    /// routes the ticket back to Backlog owned by the groomer, carrying the whole verdict history
    /// so the triage pass can re-scope from evidence rather than from a run log.
    /// </para>
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
                    && a.Conditions.OfType<RepairBudgetConditionSpec>().Any(c => c.Mode == "exhausted")
                    && ExtendedRepair(a) == ExtendedRepair(repair)
                    && !Triaged(a));

            Assert.Equal(repair.Enabled, twin.Enabled);

            // Return-to-sender, not a page: exhaustion re-scopes through the groomer.
            Assert.False(Blocks(twin), $"'{twin.Id}' must re-scope, not block.");
            Assert.Contains(twin.Actions.OfType<MoveTicketStatusActionSpec>(), m => m.To == "Backlog");
            Assert.Equal("groomer", Assert.Single(twin.Actions.OfType<AssignTicketActionSpec>()).Slug);
            Assert.Contains(
                twin.Actions.OfType<AddCommentActionSpec>(),
                c => c.Content.Contains("{verdictHistory}", StringComparison.Ordinal));
            Assert.DoesNotContain(twin.Actions, a => a is RunAgentActionSpec);

            // The pair's caps agree, so a labeled ticket cannot be "within cap" on one arm and
            // "exhausted" on the other at the same round.
            Assert.Equal(
                Assert.Single(repair.Conditions.OfType<RepairBudgetConditionSpec>()).MaxCycles,
                Assert.Single(twin.Conditions.OfType<RepairBudgetConditionSpec>()).MaxCycles);
        }
    }

    /// <summary>
    /// Phase 1.6: <c>extended-repair</c> raises the repair cap without an engine change, by
    /// duplicating each repair arm. Exactly one of the two may match any ticket, so the base arm
    /// must negate the label the extended arm requires — otherwise a labeled ticket fires both and
    /// gets two dispatches per FIX verdict.
    /// </summary>
    [Fact]
    public void The_extended_repair_label_selects_exactly_one_arm_of_each_pair()
    {
        var config = LoadConfig();
        var extended = config.Automations.Where(ExtendedRepair).ToList();
        Assert.NotEmpty(extended);

        foreach (var arm in extended)
        {
            Assert.EndsWith("-extended", arm.Id, StringComparison.Ordinal);
            var budget = Assert.Single(arm.Conditions.OfType<RepairBudgetConditionSpec>());
            Assert.Equal(4, budget.MaxCycles);

            var baseArm = Assert.Single(
                config.Automations,
                a => a.Id == arm.Id[..^"-extended".Length]);

            // The base arm reads the contract's own cap, and stands down for the label.
            Assert.Null(Assert.Single(baseArm.Conditions.OfType<RepairBudgetConditionSpec>()).MaxCycles);
            Assert.Single(
                baseArm.Conditions.OfType<LabelsConditionSpec>(),
                c => c.Negate && c.Labels.Contains("extended-repair"));
        }
    }

    private static bool ExtendedRepair(GigaClaw.Core.Automation.Automation automation)
        => automation.Conditions.OfType<LabelsConditionSpec>()
            .Any(c => !c.Negate && c.Labels.Contains("extended-repair"));

    private static bool Triaged(GigaClaw.Core.Automation.Automation automation)
        => automation.Conditions.OfType<LabelsConditionSpec>()
            .Any(c => !c.Negate && c.Labels.Contains("triaged"));

    /// <summary>
    /// The funded-loop guard. Exhaustion routes a ticket back to Backlog owned by the groomer, and
    /// both the trigger's attempt counter (reset by the groomer's own edits) and the repair budget
    /// (reset by the escalation receipt) hand the next lap fresh budgets — so without a stop
    /// condition a ticket nobody can specify would be re-scoped and re-run forever, each lap
    /// costing real money. The <c>triaged</c> label is that stop: the first exhaustion applies it
    /// and re-scopes, and a ticket already wearing it goes to the owner in Blocked instead.
    /// </summary>
    [Fact]
    public void Exhaustion_re_scopes_once_and_then_hands_the_ticket_to_a_human()
    {
        var config = LoadConfig();
        var reScoping = config.Automations
            .Where(a => a.Conditions.OfType<RepairBudgetConditionSpec>().Any(c => c.Mode == "exhausted")
                && !Triaged(a))
            .ToList();
        Assert.NotEmpty(reScoping);

        foreach (var lap in reScoping)
        {
            // It only runs on a ticket that has not had its lap yet …
            Assert.Single(
                lap.Conditions.OfType<LabelsConditionSpec>(),
                c => c.Negate && c.Labels.Contains("triaged"));

            // … and it is what marks the lap as used, before the ticket leaves the column.
            var labelAt = lap.Actions.FindIndex(a => a is SetLabelsActionSpec s && s.Add.Contains("triaged"));
            var moveAt = lap.Actions.FindIndex(a => a is MoveTicketStatusActionSpec);
            Assert.True(labelAt >= 0, $"'{lap.Id}' re-scopes without recording the triage lap.");
            Assert.True(labelAt < moveAt, $"'{lap.Id}' labels the ticket after moving it.");
            Assert.Equal("groomer", Assert.Single(lap.Actions.OfType<AssignTicketActionSpec>()).Slug);

            // The second lap exists, and it terminates on a person.
            var terminal = Assert.Single(
                config.Automations,
                a => a.Id != lap.Id
                    && Triaged(a)
                    && ExtendedRepair(a) == ExtendedRepair(lap)
                    && a.Trigger is TicketCommentAddedTriggerSpec t
                    && lap.Trigger is TicketCommentAddedTriggerSpec lt
                    && t.Authors.SequenceEqual(lt.Authors)
                    && a.Conditions.OfType<AssignedToConditionSpec>()
                        .SelectMany(c => c.Slugs)
                        .SequenceEqual(lap.Conditions.OfType<AssignedToConditionSpec>().SelectMany(c => c.Slugs)));

            Assert.True(Blocks(terminal), $"'{terminal.Id}' must block rather than re-scope again.");
            Assert.Equal("owner", Assert.Single(terminal.Actions.OfType<AssignTicketActionSpec>()).Slug);
            Assert.DoesNotContain(terminal.Actions, a => a is RunAgentActionSpec);
            Assert.DoesNotContain(
                terminal.Actions.OfType<SetLabelsActionSpec>(),
                s => s.Add.Contains("triaged"));
        }
    }

    /// <summary>
    /// A funded loop needs a floor as well as a lap count. <c>maxTicketCostUsd</c> is the per-ticket
    /// spend ceiling <c>RunStateManager</c> enforces before any dispatch; shipping it unset would
    /// mean every new project runs its triage loops on an unlimited budget until somebody notices.
    /// The default is deliberately conservative — a ceiling, not a target.
    /// </summary>
    [Fact]
    public void The_template_ships_a_per_ticket_spend_ceiling()
    {
        var config = LoadConfig();
        Assert.NotNull(config.MaxTicketCostUsd);
        Assert.InRange(config.MaxTicketCostUsd!.Value, 1m, 50m);
    }

    /// <summary>
    /// The reviewer-retry receipt is the budget, so it may only be written by the dispatch it
    /// records — <c>ActionExecutor.MintReviewerRetryReceiptAsync</c>, on the non-skip path. An
    /// <c>addComment</c> in front of the <c>runAgent</c> is committed even when the dispatch is
    /// skipped (a busy reviewer), which spends the budget for a re-review nobody was asked for and
    /// strands the ticket in Review with no arm left that matches it.
    /// </summary>
    [Fact]
    public void No_retry_arm_writes_its_own_receipt_in_config()
    {
        var config = LoadConfig();
        var retries = config.Automations
            .Where(a => a.Conditions.OfType<ReviewerRetryBudgetConditionSpec>().Any(c => c.Mode == "withinCap"))
            .ToList();
        Assert.NotEmpty(retries);

        foreach (var retry in retries)
        {
            Assert.DoesNotContain(
                retry.Actions.OfType<AddCommentActionSpec>(),
                c => c.Content.Contains(ReviewerRetry.RetryMarkerPrefix, StringComparison.Ordinal));
            Assert.IsType<RunAgentActionSpec>(retry.Actions[0]);
        }
    }

    /// <summary>
    /// The retry arms move no ticket, on purpose — the work stays in Review with its assignee. That
    /// leaves no column poll to rescue a ticket whose reviewer never answers, so the gate ships a
    /// watchdog that polls Review itself and terminates on the owner once the retry is spent.
    /// </summary>
    [Fact]
    public void A_review_watchdog_terminates_a_ticket_no_comment_will_ever_reach()
    {
        var config = LoadConfig();
        var watchdog = Assert.Single(config.Automations, a => a.Id == "verdict-gate-review-watchdog");

        var trigger = Assert.IsType<TicketInColumnTriggerSpec>(watchdog.Trigger);
        Assert.Equal(["Review"], trigger.Columns);
        Assert.True(trigger.Seconds >= 60, "The watchdog is a backstop, not a second gate — poll slowly.");
        Assert.True(trigger.MaxConsecutiveFirings > 0, "An unbounded watchdog is a loop.");

        var verdicts = Assert.Single(watchdog.Conditions.OfType<VerdictIsConditionSpec>());
        Assert.Equal(["INVALID", "STALE", "MISSING"], verdicts.Verdicts);
        Assert.Equal(
            "exhausted",
            Assert.Single(watchdog.Conditions.OfType<ReviewerRetryBudgetConditionSpec>()).Mode);

        Assert.True(Blocks(watchdog));
        Assert.Equal("owner", Assert.Single(watchdog.Actions.OfType<AssignTicketActionSpec>()).Slug);
        Assert.DoesNotContain(watchdog.Actions, a => a is RunAgentActionSpec);
    }

    /// <summary>
    /// A retry arm advertises exactly the outcomes it can actually see. <c>STALE</c> is produced by
    /// re-hashing the reviewed artifact, so an arm with <c>requireFreshArtifact: false</c> can never
    /// resolve one — naming it there documents a re-review path that cannot happen, and the whole
    /// point of the verdict list is that a reader can tell what will fire from it.
    /// </summary>
    [Fact]
    public void A_retry_arm_never_advertises_an_outcome_it_cannot_reach()
    {
        var config = LoadConfig();
        var retries = config.Automations
            .Where(a => a.Conditions.OfType<ReviewerRetryBudgetConditionSpec>().Any(c => c.Mode == "withinCap"))
            .ToList();
        Assert.NotEmpty(retries);

        foreach (var retry in retries)
        {
            var verdict = Assert.Single(retry.Conditions.OfType<VerdictIsConditionSpec>());
            Assert.Contains("INVALID", verdict.Verdicts);
            Assert.Contains("MISSING", verdict.Verdicts);
            Assert.Equal(verdict.RequireFreshArtifact, verdict.Verdicts.Contains("STALE"));
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
            // Most gate arms are comment-driven; the Review watchdog polls the column instead and
            // names no authors at all, which is trivially free of the evaluator.
            if (gate.Trigger is TicketCommentAddedTriggerSpec comments)
                Assert.DoesNotContain(comments.Authors, author => author == "evaluator");
        }
    }

    /// <summary>
    /// The AD-7 arms shipped switched off while blog-reviewer's AD-7 protocol still answered with
    /// `CONTENT-REVIEW` markers alone: enabling them then would have read as MISSING and blocked
    /// every content-pipeline draft. The protocol now emits a typed verdict beside those markers,
    /// so the arms are live. What this test pins is the condition that made switching them on safe:
    /// freshness must stay off, because under AD-7 the draft is the ticket description and
    /// content-writer deletes its scratch file before exiting, so no file can ever hash to
    /// `inputDigest`. Turn freshness on here and every AD-7 verdict resolves STALE.
    /// </summary>
    [Fact]
    public void The_ad7_arms_are_live_and_never_require_a_fresh_workspace_artifact()
    {
        var config = LoadConfig();
        var ad7 = config.Automations.Where(a => a.Id.StartsWith("content-verdict-gate-", StringComparison.Ordinal)).ToList();
        // ship + repair(×2 caps) + exhaustion(×2 caps, ×2 triage laps) + block
        // + reviewer-retry(×2 budget states).
        Assert.Equal(10, ad7.Count);

        foreach (var automation in ad7)
        {
            Assert.True(automation.Enabled, $"'{automation.Id}' must be enabled.");
            Assert.Contains("AD-7", automation.Name!, StringComparison.Ordinal);
            Assert.All(
                automation.Conditions.OfType<VerdictIsConditionSpec>(),
                c => Assert.False(c.RequireFreshArtifact));
        }
    }

    /// <summary>
    /// The AD-7 pipeline needs the same four arms every file-based pipeline has — advance, repair,
    /// escalate on a spent budget, and block. A gate missing its block arm passes a ticket on an
    /// unreadable verdict, which is the failure the gate exists to prevent.
    /// </summary>
    [Fact]
    public void The_ad7_pipeline_covers_ship_fix_exhaustion_and_block()
    {
        var config = LoadConfig();
        var ad7 = config.Automations
            .Where(a => a.Id.StartsWith("content-verdict-gate-", StringComparison.Ordinal))
            .ToDictionary(a => a.Id, StringComparer.Ordinal);

        foreach (var id in new[]
                 {
                     "content-verdict-gate-ship-to-done",
                     "content-verdict-gate-repair-round",
                     "content-verdict-gate-repair-round-extended",
                     "content-verdict-gate-repair-exhausted",
                     "content-verdict-gate-repair-exhausted-extended",
                     "content-verdict-gate-repair-exhausted-triaged",
                     "content-verdict-gate-repair-exhausted-triaged-extended",
                     "content-verdict-gate-block-escalate",
                     "content-verdict-gate-reviewer-retry",
                     "content-verdict-gate-reviewer-retry-exhausted",
                 })
        {
            Assert.True(ad7.ContainsKey(id), $"AD-7 pipeline is missing '{id}'.");
        }

        var verdicts = ad7.Values
            .SelectMany(a => a.Conditions.OfType<VerdictIsConditionSpec>())
            .SelectMany(c => c.Verdicts)
            .ToHashSet(StringComparer.Ordinal);

        // MISSING is the load-bearing one: a reviewer that answers in prose must block, not pass.
        foreach (var outcome in new[] { "SHIP", "FIX", "BLOCK", "MISSING" })
        {
            Assert.Contains(outcome, verdicts);
        }

        // Every AD-7 arm gates on blog-reviewer's own verdict, never on another agent's.
        Assert.All(
            ad7.Values.SelectMany(a => a.Conditions.OfType<VerdictIsConditionSpec>()),
            c => Assert.Equal("blog-reviewer", c.Agent));
    }

    // ── SP-3: worktree isolation + merge queue default-on for code-touching contracts ──────
    //
    // Owner decision (2026-08-01): whether a ticket-dispatched runAgent action runs isolated in a
    // per-ticket worktree, and lands through `enqueueMerge`, is derived from the dispatching
    // agent's contract — not from the ticket's type/labels. A contract is "code-touching" when its
    // `allowedWriteGlobs` (ProjectTemplate/Agents/contracts.json) reach source/build paths (e.g.
    // "**", "**/*Tests*"), as opposed to a narrow content/media/docs/board-write scope. Non-git
    // workspaces and ticketless firings fail closed under isolation, and content pipelines need
    // write -> review -> revise visibility without a merge hop, so only code-touching contracts
    // flip on.
    //
    // The template resolves to exactly two ticket-dispatched code-touching agents: `programmer`
    // (allowedWriteGlobs: ["**"]) and `qa-tester` (allowedWriteGlobs: ["**/*Tests*", "**/tests/**"]
    // — it writes test source, still a build path). `code-janitor` also declares ["**"] but is
    // schedule-dispatched (`code-janitor-nightly`, an interval trigger with no ticket behind the
    // firing) — there is no `<id>` to key a worktree branch on, so it cannot be isolated and fails
    // the dispatch closed rather than silently running in place; it is deliberately excluded from
    // the isolation set rather than flagged as a gap. `committer` and `decision-engine` also write
    // `.git/**`, but that is repository plumbing, not a source/build path, so they stay unisolated
    // board/git-write contracts like the rest.

    private static readonly string[] CodeTouchingTicketDispatchedAgents = ["programmer", "qa-tester"];

    [Fact]
    public void SP3_the_code_touching_contract_set_is_derived_from_allowedWriteGlobs()
    {
        using var contracts = JsonDocument.Parse(
            File.ReadAllText(Path.Combine(AgentsDir, "contracts.json")));
        var agents = contracts.RootElement.GetProperty("agents");

        string[] Globs(string slug) => agents.GetProperty(slug).GetProperty("allowedWriteGlobs")
            .EnumerateArray().Select(g => g.GetString()!).ToArray();

        // The two ticket-dispatched agents this rule turns on.
        Assert.Contains("**", Globs("programmer"));
        Assert.Contains("**/*Tests*", Globs("qa-tester"));
        Assert.Contains("**/tests/**", Globs("qa-tester"));

        // code-janitor reaches the same "**" ceiling as programmer but is schedule-dispatched
        // (see the note above) — it is a deliberate non-member of the isolation set, not a gap.
        Assert.Contains("**", Globs("code-janitor"));
        Assert.Empty(agents.GetProperty("code-janitor").GetProperty("ticketExit").EnumerateArray());

        // Spot-check that git-plumbing-only and content/media/board contracts do NOT reach a
        // source/build path, so the derivation has a real boundary rather than defaulting everyone
        // on. ".git/**" is repository metadata, not a source/build path.
        Assert.Equal([".git/**"], Globs("committer"));
        Assert.DoesNotContain(Globs("blog-writer"), glob => glob is "**" or ".git/**");
        Assert.DoesNotContain(Globs("ui-designer"), glob => glob is "**" or ".git/**");
        Assert.DoesNotContain(Globs("decision-engine"), glob => glob == "**");
    }

    /// <summary>
    /// Every runAgent action (after {assignee} expansion) that dispatches a code-touching agent on
    /// a ticket trigger must run isolated. This is what actually protects the contract: the shared
    /// `{assignee}` automations (assignee-dispatch, assignee-resume, owner-feedback) serve ~26
    /// agents through ONE action, so programmer/qa-tester were split into their own
    /// `*-code` twins specifically so isolation could be set without isolating everyone else
    /// riding the same automation.
    /// </summary>
    [Fact]
    public void SP3_every_ticket_dispatched_runAgent_action_for_a_code_touching_contract_is_worktree_isolated()
    {
        var config = LoadConfig();

        foreach (var automation in config.Automations)
        {
            var assigneeSlugs = automation.Conditions.OfType<AssignedToConditionSpec>()
                .SelectMany(c => c.Slugs)
                .ToHashSet(StringComparer.Ordinal);

            foreach (var run in automation.Actions.OfType<RunAgentActionSpec>())
            {
                var resolvedAgents = run.Agent == "{assignee}"
                    ? assigneeSlugs
                    : new HashSet<string>(StringComparer.Ordinal) { run.Agent };

                if (resolvedAgents.Overlaps(CodeTouchingTicketDispatchedAgents))
                {
                    Assert.True(
                        string.Equals(run.Isolation, "worktree", StringComparison.OrdinalIgnoreCase),
                        $"Automation '{automation.Id}' dispatches a code-touching agent " +
                        $"({string.Join(", ", resolvedAgents.Intersect(CodeTouchingTicketDispatchedAgents))}) " +
                        "without worktree isolation.");
                }
            }
        }
    }

    /// <summary>
    /// The flip side: no content/media/design/board dispatch picked up isolation as a side effect
    /// of the split (e.g. a stray copy-paste onto the wrong twin). Isolation is exclusively for the
    /// code-touching set.
    /// </summary>
    [Fact]
    public void SP3_no_content_media_design_or_board_dispatch_is_worktree_isolated()
    {
        var config = LoadConfig();

        foreach (var automation in config.Automations)
        {
            var assigneeSlugs = automation.Conditions.OfType<AssignedToConditionSpec>()
                .SelectMany(c => c.Slugs)
                .ToHashSet(StringComparer.Ordinal);

            foreach (var run in automation.Actions.OfType<RunAgentActionSpec>())
            {
                var resolvedAgents = run.Agent == "{assignee}"
                    ? assigneeSlugs
                    : new HashSet<string>(StringComparer.Ordinal) { run.Agent };

                if (!resolvedAgents.Overlaps(CodeTouchingTicketDispatchedAgents))
                {
                    Assert.True(
                        string.IsNullOrEmpty(run.Isolation),
                        $"Automation '{automation.Id}' sets isolation '{run.Isolation}' on a " +
                        "non-code-touching dispatch — worktree isolation is derived from the " +
                        "contract and must stay scoped to programmer/qa-tester.");
                }
            }
        }
    }

    /// <summary>
    /// code-janitor is ticketless (interval trigger, no ticket behind the firing) so there is no
    /// `&lt;id&gt;` to key a worktree branch on. It fails closed rather than being isolated —
    /// pinned here so a future edit cannot "fix" the schedule-dispatched exemption by isolating it.
    /// </summary>
    [Fact]
    public void SP3_code_janitor_is_schedule_dispatched_and_stays_unisolated()
    {
        var config = LoadConfig();
        var nightly = Assert.Single(config.Automations, a => a.Id == "code-janitor-nightly");
        Assert.IsType<IntervalTriggerSpec>(nightly.Trigger);
        Assert.Empty(nightly.Conditions.OfType<AssignedToConditionSpec>());

        var run = Assert.Single(nightly.Actions.OfType<RunAgentActionSpec>());
        Assert.Equal("code-janitor", run.Agent);
        Assert.True(string.IsNullOrEmpty(run.Isolation));
    }

    /// <summary>
    /// enqueueMerge is wired exactly where the owner decided it should be: the point a
    /// code-touching ticket's work is accepted. For the dev pipeline that is
    /// `verdict-gate-qa-ship-to-done` — the same arm that already advances the ticket out of
    /// Review on a qa-tester SHIP verdict — enqueued before the ticket closes, so a candidate
    /// with no recorded worktree branch never reaches the queue.
    /// </summary>
    [Fact]
    public void SP3_enqueueMerge_is_wired_at_the_dev_pipelines_ticket_acceptance_point()
    {
        var config = LoadConfig();
        var ship = Assert.Single(config.Automations, a => a.Id == "verdict-gate-qa-ship-to-done");

        var enqueueIndex = ship.Actions.FindIndex(a => a is EnqueueMergeActionSpec);
        var doneIndex = ship.Actions.FindIndex(a => a is MoveTicketStatusActionSpec { To: "Done" });

        Assert.True(enqueueIndex >= 0, "'verdict-gate-qa-ship-to-done' must enqueue a merge on acceptance.");
        Assert.True(doneIndex >= 0);
        Assert.True(enqueueIndex < doneIndex, "The merge must be enqueued before the ticket closes.");
    }

    /// <summary>
    /// enqueueMerge stays off every content/media/design pipeline's ship arm: those stay in-place
    /// under R4 leases, which already serialize genuinely conflicting content writes, and a merge
    /// hop would remove the write -&gt; review -&gt; revise visibility those pipelines need.
    /// <c>github-ci-success-enqueues-merge</c> (U6 follow-up (a), disabled by default — see
    /// <see cref="The_three_disabled_by_default_github_automations_ship_off_and_do_not_change_the_enabled_count"/>)
    /// is the other legitimate member: the same code-touching pipeline's merge queue, reached from
    /// GitHub CI directly instead of from the qa-tester verdict.
    /// </summary>
    [Fact]
    public void SP3_enqueueMerge_appears_nowhere_outside_the_code_touching_pipeline()
    {
        var config = LoadConfig();
        var withEnqueue = config.Automations
            .Where(a => a.Actions.OfType<EnqueueMergeActionSpec>().Any())
            .Select(a => a.Id)
            .OrderBy(id => id, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(
            new[] { "github-ci-success-enqueues-merge", "verdict-gate-qa-ship-to-done" },
            withEnqueue);
    }

    // ── U6 follow-up: GitHub automations ship wired but disabled ────────────
    //
    // Owner decision (2026-08-01): the three GitHub automations U6-EVIDENCE.md's follow-ups
    // (a)/(b)/(c) called for ship in the template WIRED BUT DISABLED — off unless a project opts in
    // by configuring a GitHub remote and token, consistent with local-first. Enabling them is a
    // per-project decision the owner makes in settings.json, not a template default.

    private static readonly string[] GitHubAutomationIds =
    [
        "github-ci-success-enqueues-merge",
        "github-ci-failure-records-check",
        "verdict-gate-qa-ship-open-pull-request",
    ];

    [Fact]
    public void The_three_disabled_by_default_github_automations_ship_off_and_do_not_change_the_enabled_count()
    {
        var config = LoadConfig();

        foreach (var id in GitHubAutomationIds)
        {
            var automation = Assert.Single(config.Automations, a => a.Id == id);
            Assert.False(automation.Enabled, $"'{id}' must ship disabled by default.");
        }

        // Same criterion CatalogGeneratorTests pins from the other side: the enabled total is
        // unchanged by these three, because every one of them starts off.
        var enabledIds = config.Automations.Where(a => a.Enabled).Select(a => a.Id).ToHashSet(StringComparer.Ordinal);
        Assert.DoesNotContain(enabledIds, id => GitHubAutomationIds.Contains(id, StringComparer.Ordinal));
    }

    [Fact]
    public void The_github_ci_success_arm_names_the_check_that_enqueues_the_merge()
    {
        var config = LoadConfig();
        var success = Assert.Single(config.Automations, a => a.Id == "github-ci-success-enqueues-merge");

        var trigger = Assert.IsType<GitHubCheckStatusTriggerSpec>(success.Trigger);
        Assert.Equal(["success"], trigger.Conclusions);

        var comment = Assert.Single(success.Actions.OfType<AddCommentActionSpec>());
        Assert.Contains("{checkName}", comment.Content, StringComparison.Ordinal);
        Assert.Contains("{checkConclusion}", comment.Content, StringComparison.Ordinal);

        var enqueueIndex = success.Actions.FindIndex(a => a is EnqueueMergeActionSpec);
        Assert.True(enqueueIndex > success.Actions.IndexOf(comment), "The check must be named before the merge is enqueued.");
    }

    /// <summary>
    /// U6 follow-up (c)'s new placeholder is exactly why leg 5's assertion in U6EndToEndTests could
    /// be strengthened: this pins the template's failure arm to the same vocabulary.
    /// </summary>
    [Fact]
    public void The_github_ci_failure_arm_names_the_failing_check_and_never_enqueues()
    {
        var config = LoadConfig();
        var failure = Assert.Single(config.Automations, a => a.Id == "github-ci-failure-records-check");

        var trigger = Assert.IsType<GitHubCheckStatusTriggerSpec>(failure.Trigger);
        Assert.Equal(["failure", "timed_out", "cancelled"], trigger.Conclusions);

        var comment = Assert.Single(failure.Actions.OfType<AddCommentActionSpec>());
        Assert.Contains("{checkName}", comment.Content, StringComparison.Ordinal);
        Assert.Contains("{checkConclusion}", comment.Content, StringComparison.Ordinal);
        Assert.Empty(failure.Actions.OfType<EnqueueMergeActionSpec>());
    }

    /// <summary>
    /// The natural-home claim in the <c>openPullRequest</c> doc comment
    /// (<see cref="OpenPullRequestActionSpec"/>): the template's PR-opening arm fires on the same
    /// qa-tester SHIP verdict as <c>verdict-gate-qa-ship-to-done</c>, so a project with a remote
    /// enables one sibling automation beside the always-on gate rather than editing it.
    /// </summary>
    [Fact]
    public void The_open_pull_request_arm_gates_on_the_same_qa_ship_verdict_as_the_enqueueMerge_gate()
    {
        var config = LoadConfig();
        var openPr = Assert.Single(config.Automations, a => a.Id == "verdict-gate-qa-ship-open-pull-request");
        var ship = Assert.Single(config.Automations, a => a.Id == "verdict-gate-qa-ship-to-done");

        var openPrTrigger = Assert.IsType<TicketCommentAddedTriggerSpec>(openPr.Trigger);
        var shipTrigger = Assert.IsType<TicketCommentAddedTriggerSpec>(ship.Trigger);
        Assert.Equal(shipTrigger.Authors, openPrTrigger.Authors);

        var openPrAssignees = Assert.Single(openPr.Conditions.OfType<AssignedToConditionSpec>());
        var shipAssignees = Assert.Single(ship.Conditions.OfType<AssignedToConditionSpec>());
        Assert.Equal(shipAssignees.Slugs, openPrAssignees.Slugs);

        var openPrVerdict = Assert.Single(openPr.Conditions.OfType<VerdictIsConditionSpec>());
        Assert.Equal(["SHIP"], openPrVerdict.Verdicts);
        Assert.Equal("qa-tester", openPrVerdict.Agent);

        Assert.Single(openPr.Actions.OfType<OpenPullRequestActionSpec>());
        Assert.Empty(openPr.Actions.OfType<EnqueueMergeActionSpec>());
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
