using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Sdk.Tests;

/// <summary>
/// Visual Studio evaluates projects with .NET Framework MSBuild. Property functions that exist only
/// on .NET Core, such as the two-argument <c>Path.GetFullPath(path, basePath)</c>, evaluate under
/// <c>dotnet build</c> and then fail every orchestration-enabled project's load in the IDE with
/// "Invalid static method invocation syntax". The shipped SDK files must stay evaluable on both.
/// </summary>
public sealed class VisualStudioMsBuildCompatibilityTests
{
    private static readonly Regex TwoArgumentGetFullPath = new(
        @"GetFullPath\s*\(\s*'[^']*'\s*,\s*'[^']*'\s*\)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>The .NET Framework MSBuild has no two-argument Path.GetFullPath overload.</summary>
    [Fact(DisplayName = "Cohesion Test [Sdk] - MSBuild files: Should not use the two-argument Path.GetFullPath that .NET Framework MSBuild lacks")]
    public void ShippedMsBuildFiles_ShouldNotUseTwoArgumentGetFullPath()
    {
        string sdks = Path.Combine(FindRepository(), "sdks");
        string[] offenders = Directory.EnumerateFiles(sdks, "*.*", SearchOption.AllDirectories)
            .Where(path => path.EndsWith(".props", System.StringComparison.OrdinalIgnoreCase)
                || path.EndsWith(".targets", System.StringComparison.OrdinalIgnoreCase))
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                && !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
            .Where(path => TwoArgumentGetFullPath.IsMatch(File.ReadAllText(path)))
            .Select(path => Path.GetRelativePath(sdks, path))
            .Order()
            .ToArray();

        offenders.ShouldBeEmpty(
            "use $([MSBuild]::NormalizePath(base, relative)) instead; Visual Studio's MSBuild cannot evaluate Path.GetFullPath(path, basePath)");
    }

    private static string FindRepository()
    {
        string? directory = Path.GetDirectoryName(typeof(VisualStudioMsBuildCompatibilityTests).Assembly.Location);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory, "global.json")) && Directory.Exists(Path.Combine(directory, "sdks")))
            {
                return directory;
            }

            directory = Path.GetDirectoryName(directory);
        }

        throw new DirectoryNotFoundException("The cohesion repository root was not found above the test assembly.");
    }
}
