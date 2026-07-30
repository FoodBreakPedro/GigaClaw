namespace GigaClaw.Core.Packs;

/// <summary>
/// Step 3 of D5's staged install: merge per file into the workspace, capturing a pre-image of
/// every file overwritten or deleted, so any failure mid-merge restores exactly what was there.
///
/// Per-file is not an implementation detail. <c>.agents/**</c> also holds live runtime state —
/// per-topic memory files written by the consolidation pass, <c>evaluator/memory/scores.json</c>,
/// <c>documentalist/memory/state.json</c>, and the owner's edited <c>automations.json</c> that the
/// automation editor saves straight back through <c>AutomationStore.SaveAsync</c>. A wholesale
/// directory swap destroys all of it, which is why §4 rejects one. This type only ever touches
/// paths it is explicitly handed.
/// </summary>
internal sealed class WorkspaceMergeTransaction
{
    private readonly string _workspace;
    private readonly List<(string Relative, byte[]? PreImage)> _touched = new();
    private readonly List<string> _createdDirectories = new();

    public WorkspaceMergeTransaction(string workspace) => _workspace = workspace;

    /// <summary>Workspace-relative destinations actually modified, in order.</summary>
    public IReadOnlyList<string> Touched => _touched.Select(t => t.Relative).ToList();

    public string FullPath(string relative) =>
        Path.Combine(_workspace, relative.Replace('/', Path.DirectorySeparatorChar));

    public async Task WriteAsync(string relative, byte[] content, CancellationToken ct = default)
    {
        var full = FullPath(relative);
        Capture(relative, full);
        EnsureDirectory(Path.GetDirectoryName(full)!);
        await File.WriteAllBytesAsync(full, content, ct);
    }

    public void Delete(string relative)
    {
        var full = FullPath(relative);
        if (!File.Exists(full)) return;
        Capture(relative, full);
        File.Delete(full);
    }

    /// <summary>
    /// Restores every captured pre-image in reverse order and removes directories this
    /// transaction created. A rejected or failed install must leave the disk as it found it.
    /// </summary>
    public void Rollback()
    {
        for (var i = _touched.Count - 1; i >= 0; i--)
        {
            var (relative, preImage) = _touched[i];
            var full = FullPath(relative);
            try
            {
                if (preImage is null)
                {
                    if (File.Exists(full)) File.Delete(full);
                }
                else
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(full)!);
                    File.WriteAllBytes(full, preImage);
                }
            }
            catch
            {
                // Best effort: one unrestorable path must not abort restoring the rest.
            }
        }

        for (var i = _createdDirectories.Count - 1; i >= 0; i--)
        {
            try
            {
                var dir = _createdDirectories[i];
                if (Directory.Exists(dir)
                    && !Directory.EnumerateFileSystemEntries(dir).Any())
                {
                    Directory.Delete(dir);
                }
            }
            catch { /* best effort */ }
        }
    }

    private void Capture(string relative, string full)
    {
        _touched.Add((relative, File.Exists(full) ? File.ReadAllBytes(full) : null));
    }

    private void EnsureDirectory(string directory)
    {
        if (Directory.Exists(directory)) return;
        // Record every level we create so rollback can unwind them bottom-up.
        var stack = new Stack<string>();
        var cursor = directory;
        while (!string.IsNullOrEmpty(cursor) && !Directory.Exists(cursor))
        {
            stack.Push(cursor);
            cursor = Path.GetDirectoryName(cursor);
        }
        while (stack.Count > 0)
        {
            var next = stack.Pop();
            Directory.CreateDirectory(next);
            _createdDirectories.Add(next);
        }
    }
}
