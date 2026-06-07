namespace BuildDiff;

/// <summary>
/// The ordered catalog of every ecosystem BuildDiff understands. Adding a stack
/// is one line here plus one provider file.
/// </summary>
public static class ProviderRegistry
{
    public static IReadOnlyList<IEnvironmentProvider> All { get; } = new IEnvironmentProvider[]
    {
        // ---- Ported from v1 (Windows / .NET heritage) ----
        new VisualStudioProvider(),
        new DotnetProvider(),
        new NuGetProvider(),
        new NativeRuntimeProvider(),

        // ---- Cross-platform ecosystems ----
        new NodeProvider(),
        new SwiftProvider(),
        new CMakeCppProvider(),
        new GoProvider(),
        new RustProvider(),
        new JvmProvider(),
        new RubyProvider(),
        new PhpProvider(),
        new PythonProvider(),
        new SwigProvider(),
        new ContainerProvider(),

        // ---- Always last: anything else on PATH ----
        new EnvironmentProvider(),
        new GenericToolchainProvider(),
    };
}
