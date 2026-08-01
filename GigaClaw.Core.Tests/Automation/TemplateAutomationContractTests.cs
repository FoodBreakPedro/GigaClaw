using System.Text.Json;
using GigaClaw.Catalog;
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
        Assert.Equal(4, ad7.Count);

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
                     "content-verdict-gate-repair-exhausted",
                     "content-verdict-gate-block-escalate",
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
    /// </summary>
    [Fact]
    public void SP3_enqueueMerge_appears_nowhere_outside_the_code_touching_pipeline()
    {
        var config = LoadConfig();
        var withEnqueue = config.Automations
            .Where(a => a.Actions.OfType<EnqueueMergeActionSpec>().Any())
            .Select(a => a.Id)
            .ToArray();

        Assert.Equal(["verdict-gate-qa-ship-to-done"], withEnqueue);
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
