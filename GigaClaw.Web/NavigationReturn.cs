namespace GigaClaw.Web;

/// <summary>
/// Builds safe, local return URLs for project pages that can be opened from
/// either the unified board or another project-specific screen.
/// </summary>
public static class NavigationReturn
{
    public static bool IsSafeLocalPath(string? path) =>
        !string.IsNullOrWhiteSpace(path)
        && path[0] == '/'
        && (path.Length == 1 || path[1] != '/')
        && !path.Contains('\\')
        && !path.Any(char.IsControl);

    public static string Resolve(string? returnTo, string fallback) =>
        IsSafeLocalPath(returnTo) ? returnTo! : fallback;

    public static string WithReturnTo(string target, string returnTo)
    {
        var safeReturnTo = Resolve(returnTo, "/");
        var separator = target.Contains('?') ? '&' : '?';
        return $"{target}{separator}returnTo={Uri.EscapeDataString(safeReturnTo)}";
    }
}
