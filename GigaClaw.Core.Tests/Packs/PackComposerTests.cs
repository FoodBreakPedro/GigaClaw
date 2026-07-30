using System.Text.Json.Nodes;
using GigaClaw.Core.Packs;
using GigaClaw.Core.Tests.Helpers;

namespace GigaClaw.Core.Tests.Packs;

/// <summary>
/// Composition rules from doc/pack-infrastructure.md §4: the declared-and-verified inventory, the
/// flat global slug namespace (D2), cross-pack references through declared dependencies only (D4),
/// the ordering contract, and the permission closure checks from §7.
/// </summary>
public sealed class PackComposerTests
{
    private static PackValidationException Refused(params IPackSource[] sources) =>
        Assert.Throws<PackValidationException>(() => PackComposer.Compose(sources));

    private static PackFixture Core(string root) =>
        PackFixture.Create(root, "core", kind: "core", removable: false).Agent("qa-tester");

    [Fact]
    public void Compose_verifies_provides_against_the_tree_in_both_directions()
    {
        using var tmp = new TempDir();

        // Declared but absent.
        var missing = PackFixture.Create(tmp.Path, "pack-missing").EvalFixtures("ghost-fixture");
        ((JsonArray)missing.Manifest["provides"]!["agents"]!).Add("ghost-agent");
        Assert.Contains(
            Refused(missing.Build()).Errors,
            e => e.Contains("declares 'ghost-agent' but it is absent"));

        // Present but undeclared.
        var extra = PackFixture.Create(tmp.Path, "pack-extra").Agent("declared-agent");
        extra.UndeclaredAgentFile("stowaway/SKILL.md");
        Assert.Contains(
            Refused(extra.Build()).Errors,
            e => e.Contains("contains 'stowaway'") && e.Contains("does not declare it"));

        var extraRoot = PackFixture.Create(tmp.Path, "pack-extra-root").Agent("a-one");
        extraRoot.UndeclaredRootFile("SNEAKY.md");
        Assert.Contains(
            Refused(extraRoot.Build()).Errors,
            e => e.Contains("contains 'SNEAKY.md'"));
    }

    [Fact]
    public void Compose_refuses_two_packs_claiming_the_same_agent_slug()
    {
        using var tmp = new TempDir();
        var left = PackFixture.Create(tmp.Path, "pack-left").Agent("shared-agent").Build();
        var right = PackFixture.Create(tmp.Path, "pack-right").Agent("shared-agent").Build();

        // D2: never namespaced. Two packs claiming one slug is a packaging bug.
        Assert.Contains(
            Refused(left, right).Errors,
            e => e.Contains("agent slug 'shared-agent' is claimed by both"));
    }

    [Fact]
    public void Compose_refuses_a_cross_pack_reference_without_a_declared_dependency()
    {
        using var tmp = new TempDir();
        var provider = PackFixture.Create(tmp.Path, "pack-provider").Agent("far-agent").Build();
        var consumer = PackFixture.Create(tmp.Path, "pack-consumer")
            .Agent("near-agent")
            .Permits(actions: new[] { "runAgent" })
            .Automations(new JsonArray(PackFixture.RunAgentAutomation("consumer-run", "far-agent")))
            .Build();

        Assert.Contains(
            Refused(provider, consumer).Errors,
            e => e.Contains("runs agent 'far-agent'") && e.Contains("dependsOn"));
    }

    [Fact]
    public void Compose_allows_a_cross_pack_reference_to_a_core_agent()
    {
        using var tmp = new TempDir();
        var core = Core(tmp.Path).Build();
        var pack = PackFixture.Create(tmp.Path, "pack-escalate")
            .Agent("incident-lead")
            .Permits(actions: new[] { "runAgent" })
            .Automations(new JsonArray(PackFixture.RunAgentAutomation("escalate", "qa-tester")))
            .Build();

        var composition = PackComposer.Compose(new[] { pack, core });

        Assert.Equal(new[] { "core", "pack-escalate" }, composition.Packs.Select(p => p.Id));
    }

    [Fact]
    public void Compose_allows_an_assignedTo_slug_from_a_declared_dependency()
    {
        using var tmp = new TempDir();
        var baseline = PackFixture.Create(tmp.Path, "pack-base").Agent("base-agent").Build();
        var dependent = PackFixture.Create(tmp.Path, "pack-dependent")
            .Agent("dependent-agent")
            .DependsOn("pack-base")
            .Permits(actions: new[] { "runAgent" })
            .Automations(new JsonArray(
                PackFixture.AssigneeDispatchAutomation("dep-dispatch", "base-agent", "dependent-agent")))
            .Build();

        var composition = PackComposer.Compose(new[] { dependent, baseline });

        Assert.Equal(new[] { "pack-base", "pack-dependent" }, composition.Packs.Select(p => p.Id));
    }

    [Fact]
    public void Compose_orders_core_first_then_dependencies_then_id_ordinal()
    {
        using var tmp = new TempDir();
        var core = Core(tmp.Path).Build();
        var zulu = PackFixture.Create(tmp.Path, "zulu").Agent("z-agent").Build();
        var alpha = PackFixture.Create(tmp.Path, "alpha").Agent("a-agent").DependsOn("zulu").Build();
        var mike = PackFixture.Create(tmp.Path, "mike").Agent("m-agent").Build();

        var composition = PackComposer.Compose(new[] { alpha, mike, zulu, core });

        // core first; then the ordinal-sorted list, skipping any pack whose dependency is unplaced.
        Assert.Equal(new[] { "core", "mike", "zulu", "alpha" }, composition.Packs.Select(p => p.Id));
    }

    [Fact]
    public void Compose_refuses_a_dependency_cycle()
    {
        using var tmp = new TempDir();
        var left = PackFixture.Create(tmp.Path, "cycle-left").Agent("l-agent").DependsOn("cycle-right").Build();
        var right = PackFixture.Create(tmp.Path, "cycle-right").Agent("r-agent").DependsOn("cycle-left").Build();

        Assert.Contains(Refused(left, right).Errors, e => e.Contains("dependency cycle"));
    }

    [Fact]
    public void Compose_refuses_a_dependency_below_the_declared_minimum_version()
    {
        using var tmp = new TempDir();
        var baseline = PackFixture.Create(tmp.Path, "pack-base", version: "1.0.0").Agent("base-agent").Build();
        var dependent = PackFixture.Create(tmp.Path, "pack-dep")
            .Agent("dep-agent").DependsOn("pack-base", "1.2.0").Build();

        Assert.Contains(
            Refused(baseline, dependent).Errors,
            e => e.Contains("requires 'pack-base' >= 1.2.0 but 1.0.0 is selected"));
    }

    [Fact]
    public void Compose_refuses_a_pack_that_requires_a_newer_pack_runtime()
    {
        using var tmp = new TempDir();
        var future = PackFixture.Create(tmp.Path, "pack-future", minRuntime: 2, maxRuntime: 2)
            .Agent("future-agent").Build();

        // §5: install of a future-runtime pack is refused outright.
        Assert.Contains(
            Refused(future).Errors,
            e => e.Contains("requires pack-runtime >= 2") && e.Contains("refused"));
    }

    [Fact]
    public void Compose_quarantines_rather_than_refusing_a_pack_below_the_current_runtime()
    {
        using var tmp = new TempDir();
        var old = PackFixture.Create(tmp.Path, "pack-old", minRuntime: 1, maxRuntime: 1)
            .Agent("old-agent").Build();

        var composition = PackComposer.Compose(new[] { old }, new PackComposeOptions(RuntimeVersion: 2));

        // Not auto-upgraded and not auto-removed: files stay, and the host force-disables it.
        var quarantined = Assert.Single(composition.Quarantined);
        Assert.Equal("pack-old", quarantined.Id);
        Assert.Equal(PackCompatibility.QuarantinedTooOld, quarantined.Compatibility);
    }

    [Fact]
    public void Compose_refuses_a_non_core_pack_carrying_contract_defaults()
    {
        using var tmp = new TempDir();
        var pack = PackFixture.Create(tmp.Path, "pack-defaults")
            .Agent("d-agent")
            .Permits(riskClasses: new[] { "code-write" }, writeGlobs: new[] { "**" })
            .Contracts(
                new JsonObject { ["d-agent"] = PackFixture.Contract("code-write", "**") },
                defaults: new JsonObject { ["maxDispatchAttempts"] = 3 })
            .Build();

        Assert.Contains(
            Refused(pack).Errors,
            e => e.Contains("carries 'defaults', which only the core pack may define"));
    }

    [Fact]
    public void Compose_refuses_a_contract_for_an_agent_the_pack_does_not_provide()
    {
        using var tmp = new TempDir();
        var pack = PackFixture.Create(tmp.Path, "pack-poach")
            .Agent("mine")
            .Permits(riskClasses: new[] { "code-write" }, writeGlobs: new[] { "**" })
            .Contracts(new JsonObject
            {
                ["mine"] = PackFixture.Contract("code-write", "**"),
                ["yours"] = PackFixture.Contract("code-write", "**"),
            })
            .Build();

        Assert.Contains(
            Refused(pack).Errors,
            e => e.Contains("contracts for 'yours', which it does not provide"));
    }

    [Fact]
    public void Compose_refuses_an_allowedWriteGlobs_entry_outside_the_manifest_ceiling()
    {
        using var tmp = new TempDir();
        var pack = PackFixture.Create(tmp.Path, "pack-smuggle")
            .Agent("smuggler")
            .Permits(riskClasses: new[] { "security-review" }, writeGlobs: new[] { "doc/security/**" })
            .Contracts(new JsonObject { ["smuggler"] = PackFixture.Contract("security-review", "**") })
            .Build();

        // §7.5: a reviewer reading only pack.json must not be surprised by a per-agent "**".
        Assert.Contains(
            Refused(pack).Errors,
            e => e.Contains("allowedWriteGlobs contains '**'") && e.Contains("ceiling"));
    }

    [Fact]
    public void Compose_refuses_an_undeclared_riskClass()
    {
        using var tmp = new TempDir();
        var pack = PackFixture.Create(tmp.Path, "pack-risk")
            .Agent("risky")
            .Permits(writeGlobs: new[] { "doc/**" })
            .Contracts(new JsonObject { ["risky"] = PackFixture.Contract("brand-new-class", "doc/**") })
            .Build();

        // Unknown risk classes fail closed in P3 enforcement, so they must be declared.
        Assert.Contains(
            Refused(pack).Errors,
            e => e.Contains("riskClass 'brand-new-class'") && e.Contains("permissions.riskClasses"));
    }

    [Fact]
    public void Compose_refuses_an_action_type_missing_from_permissions_actions()
    {
        using var tmp = new TempDir();
        var pack = PackFixture.Create(tmp.Path, "pack-action")
            .Agent("actor")
            .Automations(new JsonArray(PackFixture.RunAgentAutomation("act", "actor")))
            .Build();

        // §7.6 closure check.
        Assert.Contains(
            Refused(pack).Errors,
            e => e.Contains("uses action 'runAgent'") && e.Contains("permissions.actions"));
    }

    [Fact]
    public void Compose_refuses_a_specialist_pack_writing_a_shared_Agents_root_file()
    {
        using var tmp = new TempDir();
        var pack = PackFixture.Create(tmp.Path, "pack-preamble").Agent("p-agent");
        pack.UndeclaredAgentFile("preamble.md", "hijacked");

        // D3: preamble.md and the other shared Agents/-root files are core-owned.
        Assert.Contains(
            Refused(pack.Build()).Errors,
            e => e.Contains("only the core pack may contribute shared Agents/-root files"));
    }

    [Fact]
    public void Compose_refuses_teamMembership_naming_an_agent_the_pack_does_not_provide()
    {
        using var tmp = new TempDir();
        var core = Core(tmp.Path).Teams(new JsonArray(new JsonObject
        {
            ["slug"] = "software-engineering",
            ["name"] = "Software Engineering",
            ["agentSlugs"] = new JsonArray("qa-tester"),
        })).Build();
        var pack = PackFixture.Create(tmp.Path, "pack-member")
            .Agent("joiner")
            .TeamMembership("software-engineering", "joiner", "someone-elses-agent")
            .Build();

        Assert.Contains(
            Refused(core, pack).Errors,
            e => e.Contains("adds 'someone-elses-agent' to team 'software-engineering'"));
    }

    [Fact]
    public void Compose_refuses_an_automationPatch_targeting_a_pack_that_is_not_a_declared_dependency()
    {
        using var tmp = new TempDir();
        var other = PackFixture.Create(tmp.Path, "pack-other")
            .Agent("other-agent")
            .Permits(actions: new[] { "runAgent" })
            .Automations(new JsonArray(PackFixture.AssigneeDispatchAutomation("other-dispatch", "other-agent")))
            .Build();
        var pack = PackFixture.Create(tmp.Path, "pack-patcher")
            .Agent("patcher")
            .Patch("other-dispatch", "addAssignees", "patcher")
            .Build();

        Assert.Contains(
            Refused(other, pack).Errors,
            e => e.Contains("owned by pack 'pack-other'") && e.Contains("neither core nor a declared dependency"));
    }

    [Fact]
    public void Compose_carries_merge_artifact_fragments_rather_than_copying_the_files()
    {
        using var tmp = new TempDir();
        var pack = PackFixture.Create(tmp.Path, "pack-merge")
            .Agent("merger")
            .Permits(riskClasses: new[] { "docs-write" }, actions: new[] { "runAgent" }, writeGlobs: new[] { "doc/**" })
            .Contracts(new JsonObject { ["merger"] = PackFixture.Contract("docs-write", "doc/**") })
            .Models(new JsonObject { ["merger"] = "claude-sonnet-4-6" })
            .Teams(new JsonArray(new JsonObject
            {
                ["slug"] = "merge-team",
                ["name"] = "Merge Team",
                ["agentSlugs"] = new JsonArray("merger"),
            }))
            .Automations(new JsonArray(PackFixture.RunAgentAutomation("merge-run", "merger")))
            .Build();

        var composed = Assert.Single(PackComposer.Compose(new[] { pack }).Packs);

        Assert.Equal(new[] { "merger" }, composed.ContractAgents.Keys);
        Assert.Equal(new[] { "merger" }, composed.Models.Keys);
        Assert.Equal(new[] { "merge-team" }, composed.Teams.Keys);
        Assert.Equal(new[] { "merge-run" }, composed.Automations.Select(a => a.Id));

        // None of the four merge artifacts is carried as an opaque file — they are merged with
        // whatever the workspace already has, never copied over it.
        Assert.DoesNotContain(composed.Files, f => f.DestinationPath.EndsWith("contracts.json"));
        Assert.DoesNotContain(composed.Files, f => f.DestinationPath.EndsWith("automations.json"));
        Assert.Contains(composed.Files, f => f.DestinationPath == ".agents/merger/SKILL.md");
    }

    [Fact]
    public void Compose_resolves_a_reference_to_a_host_provided_agent_during_the_core_transition()
    {
        using var tmp = new TempDir();
        var pack = PackFixture.Create(tmp.Path, "pack-transition")
            .Agent("transitional")
            .Permits(actions: new[] { "runAgent" })
            .Automations(new JsonArray(PackFixture.RunAgentAutomation("transition-run", "qa-tester")))
            .Build();

        Assert.Contains(Refused(pack).Errors, e => e.Contains("runs agent 'qa-tester'"));

        var composition = PackComposer.Compose(
            new[] { pack },
            new PackComposeOptions(HostProvidedAgents: new HashSet<string> { "qa-tester" }));
        Assert.Single(composition.Packs);
    }
}
