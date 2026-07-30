using System.Text.Json;

namespace GigaClaw.Catalog;

internal static class Program
{
    private static int Main(string[] args)
    {
        var command = args.FirstOrDefault() ?? "generate";
        var strict = args.Contains("--strict", StringComparer.Ordinal);
        var rootArgument = args.Skip(1).FirstOrDefault(argument => !argument.StartsWith("--", StringComparison.Ordinal));
        var root = FindRepositoryRoot(rootArgument ?? Directory.GetCurrentDirectory());
        var generator = new CatalogGenerator();
        var catalog = generator.Generate(root);
        return command switch
        {
            "generate" => Generate(generator, root, catalog),
            "check" => Check(generator, root, catalog, strict),
            _ => Usage()
        };
    }

    private static int Generate(CatalogGenerator generator, string root, SystemCatalog catalog)
    {
        generator.WriteGeneratedFiles(root, catalog);
        Console.WriteLine("Generated catalog.json and doc/catalog.md.");
        return 0;
    }

    private static int Check(CatalogGenerator generator, string root, SystemCatalog catalog, bool strict)
    {
        var expectedJson = JsonSerializer.Serialize(catalog, new JsonSerializerOptions { WriteIndented = true }) + "\n";
        var expectedMarkdown = CatalogGenerator.RenderMarkdown(catalog);
        var errors = new List<string>();
        CheckFile(Path.Combine(root, "catalog.json"), expectedJson, errors);
        CheckFile(Path.Combine(root, "doc", "catalog.md"), expectedMarkdown, errors);
        var bindingGaps = CatalogGenerator.FindBindingGaps(catalog);
        if (strict) errors.AddRange(bindingGaps);
        else foreach (var gap in bindingGaps) Console.Error.WriteLine($"catalog known binding gap: {gap}");
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

    private static int Usage() { Console.Error.WriteLine("Usage: GigaClaw.Catalog [generate|check] [--strict] [repository-root]"); return 2; }
}
