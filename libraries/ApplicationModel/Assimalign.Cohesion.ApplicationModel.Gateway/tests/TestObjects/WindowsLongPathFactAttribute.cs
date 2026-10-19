using System;
using System.IO;

using Xunit;

using Assimalign.Cohesion.ApplicationModel.Gateway.Internal;

namespace Assimalign.Cohesion.ApplicationModel.Gateway.Tests;

// xUnit v2 evaluates Skip at discovery; probe the same temporary volume and path shape
// the test uses, without requiring an installed SDK, admin rights, or enabled 8.3 names.
internal sealed class WindowsLongPathFactAttribute : FactAttribute
{
    public WindowsLongPathFactAttribute(bool requiresShortPath = true)
    {
        if (!OperatingSystem.IsWindows())
        {
            Skip = "Requires Windows CreateProcess and 8.3 path handling.";
            return;
        }

        try
        {
            using var directory = new LongPathTestDirectory();
            directory.CreateExecutable(copyHost: false);
            if (requiresShortPath)
            {
                string? shortPath = LocalProcessPath.TryGetShortPath(directory.ExecutablePath);
                if (shortPath is null || shortPath.Length >= 260)
                {
                    Skip = "The temporary volume does not provide a usable 8.3 short path.";
                }
            }
        }
        catch (IOException exception)
        {
            Skip = $"The temporary file system or OS does not support the long-path fixture: {exception.Message}";
        }
        catch (UnauthorizedAccessException exception)
        {
            Skip = $"The temporary file system does not permit the long-path fixture: {exception.Message}";
        }
    }
}
