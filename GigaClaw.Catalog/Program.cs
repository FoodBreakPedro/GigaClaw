using System.Text.Json;
using GigaClaw.Core.Services;

namespace GigaClaw.Catalog;

internal static class Program
{
    private static int Main(string[] args)
    {
        var command = args.FirstOrDefault() ?? "generate";
        var strict = args.Contains("--strict", StringComparer.Ordinal);
        // §7.4: strict for packs is not conditional on core's mode. A pack that cannot meet the
        // five-binding bar on its first commit is exactly the anti-pattern the rule exists to
        // prevent, so --strict-packs gates every pack while leaving core on whatever mode it is in.
        var strictPacks = args.Contains("--strict-packs", StringComparer.Ordinal);

        string? project, projects;
        try
        {
            project = FlagValue(args, "--project");
            projects = FlagValue(args, "--projects");
        }
        catch (ArgumentException error)
        {
            Console.Error.WriteLine($"catalog: {error.Message}");
            return Usage();
        }

        // The value consumed by --project/--projects must not also be mistaken for the trailing
        // repository-root argument, which is otherwise "the first argument after the command that
        // is not itself a flag".
        var consumed = new HashSet<string>(StringComparer.Ordinal) { "--strict", "--strict-packs" };
        if (project is not null) { consumed.Add("--project"); consumed.Add(project); }
        if (projects is not null) { consumed.Add("--projects"); consumed.Add(projects); }

        var rootArgument = args.Skip(1).FirstOrDefault(argument =>
            !argument.StartsWith("--", StringComparison.Ordinal) && !consumed.Contains(argument));
        var root = FindRepositoryRoot(rootArgument ?? Directory.GetCurrentDirectory());

        if (command == "check" && (project is not null || projects is not null))
            return CheckProjects(project, projects);

        var generator = new CatalogGenerator();
        var build = generator.Build(root);
        return command switch
        {
            "generate" => Generate(generator, root, build.Catalog),
            "check" => Check(root, build, strict, strictPacks),
            _ => Usage()
        };
    }

    private static string? FlagValue(string[] args, string flag)
    {
        var index = Array.IndexOf(args, flag);
        if (index < 0) return null;
        if (index + 1 >= args.Length) throw new ArgumentException($"{flag} requires a value.");
        return args[index + 1];
    }

    /// <summary>
    /// The per-project successor to <c>tools/check-automation-drift.sh</c>: compares one or more
    /// initialized workspaces' <c>.agents/**</c> and workspace-root template files against the
    /// current embedded template, reporting per-file/per-entry drift. Never modifies the workspace
    /// — <see cref="AgentsTemplateService.InitializeAsync"/> is the only thing that refreshes it.
    /// </summary>
    private static int CheckProjects(string? project, string? projects)
    {
        var workspaces = new List<string>();
        if (project is not null) workspaces.Add(Path.GetFullPath(project));
        if (projects is not null)
        {
            var projectsRoot = Path.GetFullPath(projects);
            if (!Directory.Exists(projectsRoot))
            {
                Console.Error.WriteLine($"drift check: projects root not found: {projectsRoot}");
                return 1;
            }
            workspaces.AddRange(
                Directory.EnumerateDirectories(projectsRoot, "*", SearchOption.TopDirectoryOnly)
                    .Where(directory => Directory.Exists(Path.Combine(directory, ".agents"))));
        }

        if (workspaces.Count == 0)
        {
            Console.Error.WriteLine(
                "drift check: no workspaces to check. Use --project <workspacePath> or --projects <root>.");
            return 1;
        }

        var templateService = new AgentsTemplateService();
        var anyDrift = false;
        var totalMissing = 0;
        var totalModified = 0;
        var totalExtra = 0;
        var totalAllowlisted = 0;

        foreach (var workspace in workspaces.OrderBy(w => w, StringComparer.Ordinal))
        {
            if (!Directory.Exists(Path.Combine(workspace, ".agents")))
            {
                Console.Error.WriteLine($"drift check: no .agents directory at {workspace}");
                return 1;
            }

            var report = WorkspaceDriftChecker.Check(workspace, templateService);
            Console.WriteLine($"=== {Path.GetFileName(workspace.TrimEnd('/', '\\'))} ===");
            Console.WriteLine($"template: core v{report.TemplateVersion}");

            if (report.Allowlisted.Count > 0)
            {
                Console.WriteLine("ALLOWLISTED (intentional overrides):");
                foreach (var id in report.Allowlisted) Console.WriteLine($"  - {id} (ok)");
            }

            if (report.Drift.Count == 0)
            {
                Console.WriteLine("No drift.");
            }
            else
            {
                anyDrift = true;
                foreach (var entry in report.Drift) Console.WriteLine(entry.ToString());
            }

            totalMissing += report.Drift.Count(d => d.Kind == DriftKind.Missing);
            totalModified += report.Drift.Count(d => d.Kind == DriftKind.Modified);
            totalExtra += report.Drift.Count(d => d.Kind == DriftKind.Extra);
            totalAllowlisted += report.Allowlisted.Count;
            Console.WriteLine();
        }

        Console.WriteLine(
            $"DRIFT: missing={totalMissing} modified={totalModified} extra={totalExtra} allowlisted={totalAllowlisted}");
        return anyDrift ? 1 : 0;
    }

    private static int Generate(CatalogGenerator generator, string root, SystemCatalog catalog)
    {
        generator.WriteGeneratedFiles(root, catalog);
        Console.WriteLine("Generated catalog.json and doc/catalog.md.");
        return 0;
    }

    private static int Check(string root, CatalogBuild build, bool strict, bool strictPacks)
    {
        var catalog = build.Catalog;
        var expectedJson = JsonSerializer.Serialize(catalog, new JsonSerializerOptions { WriteIndented = true }) + "\n";
        var expectedMarkdown = CatalogGenerator.RenderMarkdown(catalog);
        var errors = new List<string>();
        CheckFile(Path.Combine(root, "catalog.json"), expectedJson, errors);
        CheckFile(Path.Combine(root, "doc", "catalog.md"), expectedMarkdown, errors);

        foreach (var gap in CatalogGenerator.FindBindingGaps(catalog))
        {
            var gated = CatalogGenerator.GatedReasons(gap, strict, strictPacks, catalog);
            if (gated.Count > 0)
                errors.Add(new BindingGap(gap.Pack, gap.Slug, gated).ToString());
            var reported = gap.Missing.Except(gated, StringComparer.Ordinal).ToArray();
            if (reported.Length > 0)
                Console.Error.WriteLine($"catalog known binding gap: {new BindingGap(gap.Pack, gap.Slug, reported)}");
        }

        // §7.5/§7.6 are pack-scoped by construction — they check a pack's data against that pack's
        // own manifest — so they follow the pack gate, not core's mode.
        foreach (var violation in build.PackPolicyViolations)
        {
            if (strict || strictPacks) errors.Add(violation.ToString());
            else Console.Error.WriteLine($"catalog known pack policy violation: {violation}");
        }

        CheckReadmeCounts(root, catalog, errors);
        if (errors.Count == 0) return 0;
        foreach (var error in errors) Console.Error.WriteLine($"catalog check: {error}");
        return 1;
    }

    private static void CheckFile(string path, string expected, List<string> errors)
    {
        if (!File.Exists(path) || File.ReadAllText(path) != expected) errors.Add($"generated file drift: {Path.GetFileName(path)} (run `dotnet run --project GigaClaw.Catalog -- generate`)");
    }

    // README count claims are opt-in: use `Catalog: N agents, M automations (E enabled)`.
    private static void CheckReadmeCounts(string root, SystemCatalog catalog, List<string> errors)
    {
        var readme = File.ReadAllText(Path.Combine(root, "README.md"));
        var marker = $"Catalog: {catalog.Summary.Agents} agents, {catalog.Summary.Automations} automations ({catalog.Summary.EnabledAutomations} enabled)";
        if (readme.Contains("Catalog:", StringComparison.Ordinal) && !readme.Contains(marker, StringComparison.Ordinal)) errors.Add("README catalog count differs from generated catalog");
    }

    private static string FindRepositoryRoot(string start)
    {
        for (var current = new DirectoryInfo(Path.GetFullPath(start)); current is not null; current = current.Parent)
            if (Directory.Exists(Path.Combine(current.FullName, "ProjectTemplate", "Agents"))) return current.FullName;
        throw new DirectoryNotFoundException("Could not find a repository root containing ProjectTemplate/Agents.");
    }

    private static int Usage()
    {
        Console.Error.WriteLine(
            "Usage: GigaClaw.Catalog [generate|check] [--strict] [--strict-packs] [repository-root]\n" +
            "       GigaClaw.Catalog check --project <workspacePath>\n" +
            "       GigaClaw.Catalog check --projects <root>   (checks every immediate subdirectory with a .agents/ folder)");
        return 2;
    }
}
