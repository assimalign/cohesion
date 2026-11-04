using System;
using System.IO;

using Xunit;

namespace Assimalign.Cohesion.Database.Sql.Tests.TestObjects;

internal sealed class LinkFactAttribute : FactAttribute
{
    public LinkFactAttribute()
    {
        string directory = Path.Combine(Path.GetTempPath(), "cohesion-link-probe-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            string target = Path.Combine(directory, "target"); File.WriteAllBytes(target, [1]);
            File.CreateSymbolicLink(Path.Combine(directory, "link"), target);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        { if (!OperatingSystem.IsWindows() || Environment.GetEnvironmentVariable("GITHUB_ACTIONS") != "true")
            { Skip = "The test process cannot create a file symbolic link: " + error.Message; } }
        finally { Directory.Delete(directory, true); }
    }
}
