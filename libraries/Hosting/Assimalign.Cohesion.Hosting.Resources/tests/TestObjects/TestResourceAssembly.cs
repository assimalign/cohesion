using System.Reflection;

namespace Assimalign.Cohesion.Hosting.Resources.Tests;

/// <summary>
/// A distinct registration key per test: <see cref="ResourceRuntime"/> registrations are process-wide
/// and cannot be removed, so each test registers against its own assembly identity.
/// </summary>
internal sealed class TestResourceAssembly : Assembly
{
    private readonly string _name;
    private readonly MethodInfo? _entryPoint;

    internal TestResourceAssembly(string name, MethodInfo? entryPoint = null)
    {
        _name = name;
        _entryPoint = entryPoint;
    }

    public override MethodInfo? EntryPoint => _entryPoint;

    public override AssemblyName GetName() => new(_name);

    public override AssemblyName GetName(bool copiedName) => GetName();
}
