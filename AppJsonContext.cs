using System.Text.Json.Serialization;

namespace BuildDiff;

/// <summary>
/// Source-generated JSON metadata for every serializable type. This is what makes the
/// tool Native-AOT-safe: no reflection-based (de)serialization at runtime. The naming
/// policy + null-omission live here so the generated code matches the old reflection
/// behavior. Nested types (VsInstall, PythonEnv, TerraformInfo, …) are pulled in
/// transitively from the registered payload roots.
/// </summary>
[JsonSourceGenerationOptions(
    WriteIndented = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(Snapshot))]
[JsonSerializable(typeof(VsPayload))]
[JsonSerializable(typeof(DotnetPayload))]
[JsonSerializable(typeof(NuGetPayload))]
[JsonSerializable(typeof(NativeRuntimePayload))]
[JsonSerializable(typeof(PythonPayload))]
[JsonSerializable(typeof(SwigInfo))]
[JsonSerializable(typeof(EnvPayload))]
[JsonSerializable(typeof(NodePayload))]
[JsonSerializable(typeof(SwiftPayload))]
[JsonSerializable(typeof(CMakeCppPayload))]
[JsonSerializable(typeof(GoPayload))]
[JsonSerializable(typeof(RustPayload))]
[JsonSerializable(typeof(JvmPayload))]
[JsonSerializable(typeof(RubyPayload))]
[JsonSerializable(typeof(PhpPayload))]
[JsonSerializable(typeof(ContainerPayload))]
[JsonSerializable(typeof(GenericPayload))]
[JsonSerializable(typeof(AndroidPayload))]
[JsonSerializable(typeof(IaCPayload))]
[JsonSerializable(typeof(BazelPayload))]
public partial class AppJsonContext : JsonSerializerContext;
