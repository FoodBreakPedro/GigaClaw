using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using GigaClaw.Core.Automation;
using AutomationRule = GigaClaw.Core.Automation.Automation;

namespace GigaClaw.Core.Packs;

/// <summary>
/// The one place a pack artifact is turned into the <c>sha256:…</c> string the lockfile stores.
/// Uninstall's whole owner-edit protection is "hash the thing on disk and compare", so install
/// and uninstall must agree on how a thing is hashed — hence a single implementation.
/// </summary>
public static class PackFileHash
{
    public static string OfBytes(ReadOnlySpan<byte> content) =>
        "sha256:" + Convert.ToHexStringLower(SHA256.HashData(content));

    /// <summary>
    /// A merge-artifact entry (one <c>contracts.json</c> agent, one <c>models.json</c> mapping,
    /// one team). Hashed from its canonical JSON text: the installer writes those same nodes back
    /// out, so parse → hash round-trips.
    /// </summary>
    public static string OfNode(JsonNode? node) =>
        OfBytes(System.Text.Encoding.UTF8.GetBytes(node?.ToJsonString() ?? "null"));

    /// <summary>
    /// One automation, hashed through the production serializer options — the same ones
    /// <c>AutomationStore.SaveAsync</c> uses when the owner edits an automation in the UI, so an
    /// owner edit is exactly what shows up as a hash difference.
    /// </summary>
    public static string OfAutomation(AutomationRule automation) =>
        OfBytes(JsonSerializer.SerializeToUtf8Bytes(automation, AutomationStore.JsonOptions));
}
