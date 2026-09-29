using System;

namespace Assimalign.Cohesion.Templates.Tests;

/// <summary>Owns the isolated package installation used by the template acceptance tests.</summary>
// Deviates from the repo interface-first rule per work-item acceptance requirements: xUnit constructs public fixtures.
public sealed class TemplatePackageFixture : IDisposable
{
    internal TemplateWorkspace Workspace { get; } = new();

    /// <summary>Packs and installs the templates in a private CLI home and NuGet cache.</summary>
    /// <exception cref="InvalidOperationException">The package cannot be packed or installed.</exception>
    public TemplatePackageFixture()
    {
        try
        {
            Workspace.InstallAsync().GetAwaiter().GetResult();
        }
        catch
        {
            Workspace.Dispose();
            throw;
        }
    }

    /// <summary>Uninstalls the package and releases the private package cache.</summary>
    public void Dispose() => Workspace.Dispose();
}
