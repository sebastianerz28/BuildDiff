using System.Text.Json;
using System.Text.Json.Nodes;

namespace BuildDiff;

/// <summary>
/// Loads a snapshot from JSON, transparently upgrading schema-v1 (the original
/// Windows/.NET flat shape) into the v2 provider map so old snapshots and the
/// existing sample fixtures still compare.
/// </summary>
public static class SnapshotUpgrader
{
    public static Snapshot Load(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        // v2 snapshots carry a "providers" object; anything else is treated as v1.
        if (root.TryGetProperty("providers", out _))
            return JsonSerializer.Deserialize<Snapshot>(json, Json.Options)!;

        return UpgradeV1(root);
    }

    private static Snapshot UpgradeV1(JsonElement root)
    {
        var snap = new Snapshot
        {
            Machine = Str(root, "machine") ?? "",
            CapturedAt = Str(root, "captured_at") ?? "",
            SchemaVersion = 2,
            Os = new OsInfo
            {
                Platform = "windows", // v1 only ever ran on Windows
                Version = root.TryGetProperty("os", out var os) ? Str(os, "version") ?? "" : "",
                Arch = root.TryGetProperty("os", out var os2) ? Str(os2, "arch") ?? "" : "",
            },
        };

        // visual-studio: assemble installs + toolsets + windows_sdks + msbuild.
        var vs = new JsonObject();
        if (TryGet(root, "visual_studio", out var vsEl)) vs["installs"] = Clone(vsEl);
        if (TryGet(root, "toolsets", out var tsEl)) vs["toolsets"] = Clone(tsEl);
        if (TryGet(root, "windows_sdks", out var sdkEl)) vs["windows_sdks"] = Clone(sdkEl);
        if (TryGet(root, "ms_build", out var mbEl)) vs["ms_build"] = Clone(mbEl);
        else if (TryGet(root, "msbuild", out var mb2)) vs["ms_build"] = Clone(mb2);
        if (vs.Count > 0) Put(snap, "visual-studio", vs);

        Copy(root, "dotnet", snap, "dotnet");
        Copy(root, "nuget", snap, "nuget");
        Copy(root, "env", snap, "environment");
        if (TryGet(root, "swig", out var swigEl)) Put(snap, "swig", Clone(swigEl));

        if (TryGet(root, "python", out var pyEl))
            Put(snap, "python", new JsonObject { ["envs"] = Clone(pyEl) });
        if (TryGet(root, "native_deps", out var ndEl))
            Put(snap, "native-runtime", new JsonObject { ["deps"] = Clone(ndEl) });

        return snap;
    }

    private static void Copy(JsonElement root, string srcKey, Snapshot snap, string providerId)
    {
        if (TryGet(root, srcKey, out var el)) Put(snap, providerId, Clone(el));
    }

    private static void Put(Snapshot snap, string providerId, JsonNode? node)
    {
        if (node is null) return;
        snap.Providers[providerId] = JsonSerializer.SerializeToElement(node, Json.Options);
    }

    private static bool TryGet(JsonElement obj, string name, out JsonElement el)
        => obj.TryGetProperty(name, out el) && el.ValueKind != JsonValueKind.Null;

    private static JsonNode? Clone(JsonElement el) => JsonNode.Parse(el.GetRawText());

    private static string? Str(JsonElement obj, string name)
        => obj.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.String ? el.GetString() : null;
}
