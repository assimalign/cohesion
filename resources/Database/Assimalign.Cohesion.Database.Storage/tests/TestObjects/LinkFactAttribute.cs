using System;
using System.IO;

using Xunit;

namespace Assimalign.Cohesion.Database.Storage.Tests.TestObjects;

internal sealed class LinkFactAttribute : FactAttribute
{
    public LinkFactAttribute()
    {
        string directory = Path.Combine(Path.GetTempPath(), "cohesion-link-probe-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            string target = Path.Combine(directory, "target"); File.WriteAllBytes(target, [1]);
            try { File.CreateSymbolicLink(Path.Combine(directory, "link"), target); }
            catch (IOException error) when (OperatingSystem.IsWindows() && (error.HResult & 0xffff) == 1314)
            {
                if (Environment.GetEnvironmentVariable("GITHUB_ACTIONS") != "true")
                { Skip = "Windows file symbolic links require a privilege unavailable to this process: " + error.Message; }
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
            { } // Leave Skip unset so the test body reports the failure.
        }
        finally { Directory.Delete(directory, true); }
    }
}
