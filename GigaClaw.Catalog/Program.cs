using System.Text.Json;

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
        var rootArgument = args.Skip(1).FirstOrDefault(argument => !argument.StartsWith("--", StringComparison.Ordinal));
        var root = FindRepositoryRoot(rootArgument ?? Directory.GetCurrentDirectory());
        var generator = new CatalogGenerator();
        var build = generator.Build(root);
        return command switch
        {
            "generate" => Generate(generator, root, build.Catalog),
            "check" => Check(root, build, strict, strictPacks),
            _ => Usage()
        };
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

    private static int Usage() { Console.Error.WriteLine("Usage: GigaClaw.Catalog [generate|check] [--strict] [--strict-packs] [repository-root]"); return 2; }
}
