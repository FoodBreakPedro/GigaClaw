using GigaClaw.Core.Github;
using GigaClaw.Core.Services;

namespace GigaClaw.Core.Automation.Triggers;

/// <summary>
/// Everything the GitHub triggers need that the trigger contract does not already carry:
/// the gated API client, the owner's settings (config + token), and the issue↔ticket mapping.
/// <para>
/// Bundled into one optional dependency so <c>ProjectRuntimeManager</c> can be constructed without
/// it — a host that never registers the GitHub services (or a test that does not care) builds
/// every other trigger exactly as before, and a GitHub trigger declared without this bundle
/// degrades to a no-op rather than throwing at config load.
/// </para>
/// </summary>
public sealed record GitHubTriggerServices(
    GitHubApiClient Client,
    AppSettingsService Settings,
    GitHubIssueLinkStore Links);
