using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace BuildDiff;

/// <summary>
/// Computes a stable content fingerprint over a snapshot's build-relevant state.
///
/// Inputs: os.platform + os.arch + the entire providers map. EXCLUDED by construction:
/// captured_at (volatile), machine name/id and tool_version (envelope/identity concerns),
/// and the whole project section (scope is recorded in CaptureMeta instead). Object keys
/// are sorted recursively and arrays are canonically sorted, so list-order churn and JSON
/// formatting never register as drift. Redacted values are already the constant
/// "&lt;redacted&gt;", so a secret never feeds the hash.
///
/// Result: two captures of an unchanged machine hash identically (dedup); a single string
/// compare against the last-seen fingerprint answers "did this machine's build env change?".
/// </summary>
public static class Fingerprinter
{
    public const int InputsVersion = 1;

    private static readonly JsonSerializerOptions Compact = new() { WriteIndented = false };

    public static Fingerprint Compute(Snapshot s)
    {
        var input = new JsonObject
        {
            ["os"] = new JsonObject { ["platform"] = s.Os.Platform, ["arch"] = s.Os.Arch },
            ["providers"] = ProvidersNode(s.Providers),
        };

        var canonical = Canon(input)!.ToJsonString(Compact);
        var hex = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
        return new Fingerprint { Algorithm = "sha256", Value = "sha256:" + hex, InputsVersion = InputsVersion };
    }

    private static JsonObject ProvidersNode(Dictionary<string, JsonElement> providers)
    {
        var obj = new JsonObject();
        foreach (var kv in providers)
        {
            try { obj[kv.Key] = JsonNode.Parse(kv.Value.GetRawText()); }
            catch { /* skip an unparseable section rather than fail the whole fingerprint */ }
        }
        return obj;
    }

    // Recursively sort object keys and arrays so semantically-equal state serializes identically.
    private static JsonNode? Canon(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject o:
                var sorted = new JsonObject();
                foreach (var kv in o.OrderBy(k => k.Key, StringComparer.Ordinal))
                    sorted[kv.Key] = Canon(kv.Value?.DeepClone());
                return sorted;
            case JsonArray a:
                var items = a.Select(x => Canon(x?.DeepClone())).ToList();
                items.Sort((x, y) => string.CompareOrdinal(
                    x?.ToJsonString(Compact) ?? "null", y?.ToJsonString(Compact) ?? "null"));
                var arr = new JsonArray();
                foreach (var it in items) arr.Add(it);
                return arr;
            default:
                return node;
        }
    }
}
