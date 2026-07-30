using System.Text.Json.Nodes;
using GigaClaw.Core.Packs;

namespace GigaClaw.Core.Tests.Packs;

/// <summary>
/// The manifest half of doc/pack-infrastructure.md §3. Every rule decidable from a single
/// pack.json is decided here, and a malformed manifest is refused rather than best-effort parsed.
/// </summary>
public sealed class PackManifestParserTests
{
    private static JsonObject Valid() => new()
    {
        ["schemaVersion"] = 1,
        ["id"] = "security-assurance",
        ["name"] = "Security Assurance",
        ["description"] = "Adversarial security review.",
        ["version"] = "1.0.0",
        ["kind"] = "specialist",
        ["removable"] = true,
        ["requiresRuntime"] = new JsonObject { ["min"] = 1, ["max"] = 1 },
        ["provides"] = new JsonObject { ["agents"] = new JsonArray("security-auditor") },
        ["permissions"] = new JsonObject
        {
            ["riskClasses"] = new JsonArray("security-review"),
            ["actions"] = new JsonArray("runAgent"),
            ["network"] = "none",
            ["allowedWriteGlobs"] = new JsonArray("doc/security/**"),
        },
        ["evalFixtures"] = new JsonArray("security-injection-in-review"),
    };

    private static IReadOnlyList<string> Errors(JsonObject manifest, string? expectedId = "security-assurance")
    {
        Assert.False(PackManifestParser.TryParse(manifest.ToJsonString(), expectedId, out _, out var errors));
        return errors;
    }

    [Fact]
    public void Parse_accepts_a_minimal_valid_manifest()
    {
        var manifest = PackManifestParser.Parse(Valid().ToJsonString(), "security-assurance");

        Assert.Equal("security-assurance", manifest.Id);
        Assert.Equal(PackKind.Specialist, manifest.Kind);
        Assert.True(manifest.Removable);
        Assert.Equal(new PackRuntimeRequirement(1, 1), manifest.RequiresRuntime);
        Assert.Equal(new[] { "security-auditor" }, manifest.Provides.Agents);
        Assert.Equal(PackPermissions.NetworkNone, manifest.Permissions.Network);
        Assert.Empty(manifest.DependsOn);
        Assert.Empty(manifest.AutomationPatches);
    }

    [Fact]
    public void Parse_refuses_a_higher_schemaVersion_without_reporting_anything_else()
    {
        var manifest = Valid();
        manifest["schemaVersion"] = 2;
        manifest.Remove("permissions"); // would be a second error at schemaVersion 1

        var errors = Errors(manifest);

        // Refused, not best-effort parsed: the rest of the document is written against a field
        // list this build does not know, so reporting field errors from it would mislead.
        var only = Assert.Single(errors);
        Assert.Contains("schemaVersion 2 is not supported", only);
    }

    [Fact]
    public void Parse_refuses_malformed_json_and_fails_closed()
    {
        Assert.False(PackManifestParser.TryParse("{ not json", "x", out var manifest, out var errors));
        Assert.Null(manifest);
        Assert.Contains(errors, e => e.Contains("not valid JSON"));
    }

    [Fact]
    public void Parse_refuses_an_id_that_does_not_match_the_directory_name()
    {
        var errors = Errors(Valid(), expectedId: "security");
        Assert.Contains(errors, e => e.Contains("must equal the directory name 'security'"));
    }

    [Theory]
    [InlineData("Security")]          // uppercase
    [InlineData("1security")]         // leading digit
    [InlineData("s")]                 // too short
    [InlineData("security_assurance")] // underscore: Member.ToSlug has no notion of it
    public void Parse_refuses_an_id_outside_the_slug_grammar(string id)
    {
        var manifest = Valid();
        manifest["id"] = id;
        Assert.Contains(Errors(manifest, expectedId: id), e => e.Contains("must match"));
    }

    [Theory]
    [InlineData("1.0.0-beta")]
    [InlineData("1.0.0+build7")]
    [InlineData("1.0")]
    [InlineData("v1.0.0")]
    public void Parse_refuses_a_version_that_is_not_bare_semver(string version)
    {
        var manifest = Valid();
        manifest["version"] = version;
        Assert.Contains(Errors(manifest), e => e.Contains("must be semver MAJOR.MINOR.PATCH"));
    }

    [Fact]
    public void Parse_refuses_a_core_pack_that_declares_itself_removable()
    {
        var manifest = Valid();
        manifest["kind"] = "core";
        manifest["removable"] = true;
        Assert.Contains(Errors(manifest), e => e.Contains("must declare removable:false"));
    }

    [Fact]
    public void Parse_refuses_an_inverted_runtime_range()
    {
        var manifest = Valid();
        manifest["requiresRuntime"] = new JsonObject { ["min"] = 3, ["max"] = 1 };
        Assert.Contains(Errors(manifest), e => e.Contains("requiresRuntime.max"));
    }

    [Theory]
    [InlineData(">=1.0.0 <2.0.0")]
    [InlineData("^1.0.0")]
    [InlineData("~1.2")]
    public void Parse_refuses_dependsOn_range_grammar(string minVersion)
    {
        var manifest = Valid();
        manifest["dependsOn"] = new JsonArray(new JsonObject { ["id"] = "core", ["minVersion"] = minVersion });
        // D7: minimum only, no range grammar anywhere.
        Assert.Contains(Errors(manifest), e => e.Contains("no range grammar"));
    }

    [Fact]
    public void Parse_refuses_a_pack_that_depends_on_itself()
    {
        var manifest = Valid();
        manifest["dependsOn"] = new JsonArray(
            new JsonObject { ["id"] = "security-assurance", ["minVersion"] = "1.0.0" });
        Assert.Contains(Errors(manifest), e => e.Contains("cannot depend on itself"));
    }

    [Theory]
    [InlineData("removeAssignees")]
    [InlineData("reorder")]
    [InlineData("setTrigger")]
    [InlineData("replaceLabels")]
    public void Parse_refuses_an_automationPatch_op_outside_the_v1_set_addition_vocabulary(string op)
    {
        var manifest = Valid();
        manifest["automationPatches"] = new JsonArray(new JsonObject
        {
            ["automation"] = "assignee-dispatch",
            ["op"] = op,
            ["slugs"] = new JsonArray("security-auditor"),
        });

        // D4: both v1 ops are set additions, which is what makes uninstall reversible as a set
        // subtraction. Reordering, removal and trigger edits are refused.
        Assert.Contains(Errors(manifest), e => e.Contains("v1 accepts set additions only"));
    }

    [Fact]
    public void Parse_refuses_addAssignees_without_slugs_and_addLabels_without_labels()
    {
        var manifest = Valid();
        manifest["automationPatches"] = new JsonArray(
            new JsonObject { ["automation"] = "a", ["op"] = "addAssignees" },
            new JsonObject { ["automation"] = "b", ["op"] = "addLabels" });

        var errors = Errors(manifest);
        Assert.Contains(errors, e => e.Contains("addAssignees on 'a' requires a non-empty slugs[]"));
        Assert.Contains(errors, e => e.Contains("addLabels on 'b' requires a non-empty labels[]"));
    }

    [Fact]
    public void Parse_refuses_declared_network_without_hosts_and_none_with_hosts()
    {
        var declared = Valid();
        ((JsonObject)declared["permissions"]!)["network"] = "declared";
        Assert.Contains(Errors(declared), e => e.Contains("networkHosts is empty"));

        var none = Valid();
        ((JsonObject)none["permissions"]!)["networkHosts"] = new JsonArray("api.osv.dev");
        Assert.Contains(Errors(none), e => e.Contains("permissions.network is \"none\""));
    }

    [Fact]
    public void Parse_accepts_declared_network_with_hosts()
    {
        var manifest = Valid();
        var permissions = (JsonObject)manifest["permissions"]!;
        permissions["network"] = "declared";
        permissions["networkHosts"] = new JsonArray("api.osv.dev", "api.github.com");

        var parsed = PackManifestParser.Parse(manifest.ToJsonString(), "security-assurance");
        Assert.Equal(PackPermissions.NetworkDeclared, parsed.Permissions.Network);
        Assert.Equal(new[] { "api.osv.dev", "api.github.com" }, parsed.Permissions.NetworkHosts);
    }

    [Fact]
    public void Parse_refuses_a_missing_required_block_rather_than_defaulting_it()
    {
        var manifest = Valid();
        manifest.Remove("permissions");
        manifest.Remove("evalFixtures");

        var errors = Errors(manifest);
        Assert.Contains(errors, e => e.Contains("permissions is required"));
        Assert.Contains(errors, e => e.Contains("evalFixtures is required"));
    }

    [Fact]
    public void Parse_refuses_a_provides_path_that_escapes_the_pack()
    {
        var manifest = Valid();
        ((JsonObject)manifest["provides"]!)["scripts"] = new JsonArray("../../etc/passwd");
        ((JsonObject)manifest["provides"]!)["rootFiles"] = new JsonArray("/etc/hosts");

        var errors = Errors(manifest);
        Assert.Contains(errors, e => e.Contains("must be a relative path inside Agents/"));
        Assert.Contains(errors, e => e.Contains("must be relative to the workspace root"));
    }

    [Fact]
    public void Parse_reports_every_problem_at_once()
    {
        var manifest = Valid();
        manifest["version"] = "nope";
        manifest["kind"] = "plugin";
        manifest.Remove("description");

        var errors = Errors(manifest);
        Assert.True(errors.Count >= 3, string.Join(" | ", errors));
    }
}
