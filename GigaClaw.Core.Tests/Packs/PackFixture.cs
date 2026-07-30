using System.Text.Json.Nodes;
using GigaClaw.Core.Packs;

namespace GigaClaw.Core.Tests.Packs;

/// <summary>
/// Minimal on-disk packs for the T6 tests. Deliberately synthetic: real pack content is G6's
/// authoring work, and a test that depended on it would fail for authoring reasons rather than
/// infrastructure ones.
/// </summary>
internal sealed class PackFixture
{
    private readonly string _root;
    private readonly JsonObject _manifest;

    private PackFixture(string root, JsonObject manifest)
    {
        _root = root;
        _manifest = manifest;
        Directory.CreateDirectory(root);
    }

    public string Id => _manifest["id"]!.GetValue<string>();
    public string Path => _root;

    public static PackFixture Create(
        string packsRoot,
        string id,
        string version = "1.0.0",
        string kind = "specialist",
        bool removable = true,
        int minRuntime = 1,
        int maxRuntime = 1)
    {
        var manifest = new JsonObject
        {
            ["schemaVersion"] = 1,
            ["id"] = id,
            ["name"] = id,
            ["description"] = $"Fixture pack {id}.",
            ["version"] = version,
            ["kind"] = kind,
            ["removable"] = removable,
            ["requiresRuntime"] = new JsonObject { ["min"] = minRuntime, ["max"] = maxRuntime },
            ["provides"] = new JsonObject { ["agents"] = new JsonArray() },
            ["permissions"] = new JsonObject
            {
                ["riskClasses"] = new JsonArray(),
                ["actions"] = new JsonArray(),
                ["network"] = "none",
                ["allowedWriteGlobs"] = new JsonArray(),
            },
            ["evalFixtures"] = new JsonArray(),
        };
        return new PackFixture(System.IO.Path.Combine(packsRoot, id), manifest);
    }

    public JsonObject Manifest => _manifest;

    private JsonObject Provides => (JsonObject)_manifest["provides"]!;
    private JsonObject Permissions => (JsonObject)_manifest["permissions"]!;

    public PackFixture DependsOn(string id, string minVersion = "1.0.0")
    {
        var list = _manifest["dependsOn"] as JsonArray;
        if (list is null)
        {
            list = new JsonArray();
            _manifest["dependsOn"] = list;
        }
        list.Add(new JsonObject { ["id"] = id, ["minVersion"] = minVersion });
        return this;
    }

    /// <summary>Adds an agent directory with a SKILL.md, a memory index, and declares the slug.</summary>
    public PackFixture Agent(string slug, string skill = "# skill\n")
    {
        WriteAgentFile($"{slug}/SKILL.md", skill);
        WriteAgentFile($"{slug}/memory/MEMORY.md", $"# {slug} memory index\n");
        ((JsonArray)Provides["agents"]!).Add(slug);
        // Every provided agent needs an eval fixture (§7's fifth binding), so the fixture builder
        // ships one by default; tests that care about the rule set the list explicitly.
        Append(_manifest, "evalFixtures", $"{slug}-fixture");
        return this;
    }

    public PackFixture Script(string name, string content = "print('hi')\n")
    {
        WriteAgentFile($"scripts/{name}", content);
        Append(Provides, "scripts", $"scripts/{name}");
        return this;
    }

    public PackFixture RootFile(string relative, string content)
    {
        WriteRootFile(relative, content);
        Append(Provides, "rootFiles", relative);
        return this;
    }

    public PackFixture Contracts(JsonObject agents, JsonObject? defaults = null)
    {
        var root = new JsonObject { ["version"] = 1, ["agents"] = agents };
        if (defaults is not null) root["defaults"] = defaults;
        WriteAgentFile(PackComposer.ContractsFile, root.ToJsonString());
        return this;
    }

    public PackFixture Models(JsonObject map)
    {
        WriteAgentFile(PackComposer.ModelsFile, map.ToJsonString());
        return this;
    }

    public PackFixture Teams(JsonArray teams)
    {
        WriteAgentFile(PackComposer.TeamsFile, teams.ToJsonString());
        foreach (var team in teams.OfType<JsonObject>())
            Append(Provides, "teams", team["slug"]!.GetValue<string>());
        return this;
    }

    public PackFixture Automations(JsonArray automations)
    {
        WriteAgentFile(
            PackComposer.AutomationsFile,
            new JsonObject { ["automations"] = automations }.ToJsonString());
        foreach (var automation in automations.OfType<JsonObject>())
            Append(Provides, "automations", automation["id"]!.GetValue<string>());
        return this;
    }

    public PackFixture Permits(
        string[]? actions = null, string[]? riskClasses = null, string[]? writeGlobs = null)
    {
        foreach (var action in actions ?? Array.Empty<string>()) Append(Permissions, "actions", action);
        foreach (var risk in riskClasses ?? Array.Empty<string>()) Append(Permissions, "riskClasses", risk);
        foreach (var glob in writeGlobs ?? Array.Empty<string>()) Append(Permissions, "allowedWriteGlobs", glob);
        return this;
    }

    public PackFixture TeamMembership(string team, params string[] slugs)
    {
        var map = _manifest["teamMembership"] as JsonObject;
        if (map is null)
        {
            map = new JsonObject();
            _manifest["teamMembership"] = map;
        }
        map[team] = new JsonArray(slugs.Select(s => (JsonNode)JsonValue.Create(s)!).ToArray());
        return this;
    }

    public PackFixture Patch(string automation, string op, params string[] values)
    {
        var list = _manifest["automationPatches"] as JsonArray;
        if (list is null)
        {
            list = new JsonArray();
            _manifest["automationPatches"] = list;
        }
        var patch = new JsonObject { ["automation"] = automation, ["op"] = op };
        var array = new JsonArray(values.Select(v => (JsonNode)JsonValue.Create(v)!).ToArray());
        patch[op == "addLabels" ? "labels" : "slugs"] = array;
        list.Add(patch);
        return this;
    }

    public PackFixture EvalFixtures(params string[] ids)
    {
        foreach (var id in ids) Append(_manifest, "evalFixtures", id);
        return this;
    }

    /// <summary>Writes a file into <c>Agents/</c> WITHOUT declaring it — used to prove the
    /// declared-and-verified rule catches an undeclared artifact.</summary>
    public PackFixture UndeclaredAgentFile(string relative, string content = "x")
    {
        WriteAgentFile(relative, content);
        return this;
    }

    /// <summary>Writes a root file WITHOUT declaring it in <c>provides.rootFiles</c>.</summary>
    public PackFixture UndeclaredRootFile(string relative, string content = "x")
    {
        WriteRootFile(relative, content);
        return this;
    }

    public DirectoryPackSource Build()
    {
        File.WriteAllText(System.IO.Path.Combine(_root, "pack.json"), _manifest.ToJsonString());
        return new DirectoryPackSource(_root);
    }

    private void WriteAgentFile(string relative, string content) =>
        WriteRootFile("Agents/" + relative, content);

    private void WriteRootFile(string relative, string content)
    {
        var full = System.IO.Path.Combine(_root, relative.Replace('/', System.IO.Path.DirectorySeparatorChar));
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
    }

    private static void Append(JsonObject parent, string property, string value)
    {
        if (parent[property] is not JsonArray array)
        {
            array = new JsonArray();
            parent[property] = array;
        }
        array.Add(value);
    }

    // ---------------------------------------------------------------- shared JSON snippets

    public static JsonObject Contract(string riskClass, params string[] writeGlobs) => new()
    {
        ["dispatches"] = new JsonArray("assignment"),
        ["ticketExit"] = new JsonArray("Review"),
        ["allowedWriteGlobs"] = new JsonArray(writeGlobs.Select(g => (JsonNode)JsonValue.Create(g)!).ToArray()),
        ["riskClass"] = riskClass,
    };

    public static JsonObject RunAgentAutomation(string id, string agent, string[]? labels = null) => new()
    {
        ["id"] = id,
        ["name"] = id,
        ["enabled"] = true,
        ["trigger"] = new JsonObject { ["type"] = "statusChange", ["pollSeconds"] = 30, ["to"] = "Review" },
        ["conditions"] = labels is null
            ? new JsonArray()
            : new JsonArray(new JsonObject
            {
                ["type"] = "labels",
                ["labels"] = new JsonArray(labels.Select(l => (JsonNode)JsonValue.Create(l)!).ToArray()),
            }),
        ["actions"] = new JsonArray(new JsonObject
        {
            ["type"] = "runAgent",
            ["agent"] = agent,
            ["maxTurns"] = 40,
        }),
    };

    /// <summary>An automation shaped like core's <c>assignee-dispatch</c>: one assignedTo roster
    /// condition, which is what <c>automationPatches.addAssignees</c> extends.</summary>
    public static JsonObject AssigneeDispatchAutomation(string id, params string[] slugs) => new()
    {
        ["id"] = id,
        ["name"] = id,
        ["enabled"] = true,
        ["trigger"] = new JsonObject { ["type"] = "ticketInColumn", ["pollSeconds"] = 30 },
        ["conditions"] = new JsonArray(new JsonObject
        {
            ["type"] = "assignedTo",
            ["slugs"] = new JsonArray(slugs.Select(s => (JsonNode)JsonValue.Create(s)!).ToArray()),
        }),
        ["actions"] = new JsonArray(new JsonObject
        {
            ["type"] = "runAgent",
            ["agent"] = "{assignee}",
            ["maxTurns"] = 60,
        }),
    };
}
