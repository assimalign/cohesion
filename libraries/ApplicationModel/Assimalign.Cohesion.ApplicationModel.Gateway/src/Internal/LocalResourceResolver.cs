using System;
using System.IO;

namespace Assimalign.Cohesion.ApplicationModel.Gateway;

/// <summary>
/// Resolves a resource's artifact identity to an executable path adjacent to the orchestrator.
/// </summary>
/// <remarks>
/// The .NET SDK emits an apphost named <c>{artifact}.exe</c> on Windows and <c>{artifact}</c>
/// elsewhere. A <c>dotnet run</c>-against-project development fallback is a planned follow-up.
/// </remarks>
internal sealed class LocalResourceResolver
{
    private readonly string _baseDirectory;

    public LocalResourceResolver(string baseDirectory)
    {
        _baseDirectory = baseDirectory;
    }

    public string Resolve(string artifact)
    {
        string executableName = OperatingSystem.IsWindows() ? artifact + ".exe" : artifact;
        string candidate = Path.Combine(_baseDirectory, executableName);

        if (File.Exists(candidate))
        {
            return candidate;
        }

        throw new FileNotFoundException(
            $"Could not resolve an executable for artifact '{artifact}'. Expected '{executableName}' in '{_baseDirectory}'.");
    }
}
