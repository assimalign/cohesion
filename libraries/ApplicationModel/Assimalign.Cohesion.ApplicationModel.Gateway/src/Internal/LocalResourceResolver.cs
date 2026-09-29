using System;
using System.IO;

namespace Assimalign.Cohesion.ApplicationModel.Gateway.Internal;

/// <summary>
/// Resolves an apphost or explicitly added executable without falling back to a managed DLL.
/// </summary>
/// <remarks>
/// A <c>dotnet run</c>-against-project development fallback is a planned follow-up.
/// </remarks>
internal sealed class LocalResourceResolver
{
    private readonly string _baseDirectory;

    public LocalResourceResolver(string baseDirectory)
    {
        _baseDirectory = baseDirectory;
    }

    public string ResolveAppHost(string appHost)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(appHost);
        return ResolvePath(appHost, "apphost");
    }

    public string ResolveExecutable(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return ResolvePath(path, "executable");
    }

    private string ResolvePath(string path, string description)
    {
        string candidate = Path.IsPathFullyQualified(path)
            ? Path.GetFullPath(path)
            : Path.GetFullPath(Path.Combine(_baseDirectory, path));

        if (File.Exists(candidate))
        {
            return candidate;
        }

        throw new FileNotFoundException(
            $"Could not resolve the local resource {description} '{path}'. Expected '{candidate}'.",
            candidate);
    }
}
