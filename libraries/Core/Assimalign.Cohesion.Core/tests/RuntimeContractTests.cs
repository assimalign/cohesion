using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Core.Tests;

public class RuntimeContractTests
{
    private const string DisplayPrefix = "Cohesion Test [Core] - RuntimeContract: ";
    private static readonly string[] ExpectedVariables =
    [
        "COHESION_APPLICATION",
        "COHESION_RESOURCE",
        "COHESION_GATEWAY",
        "COHESION_ENVIRONMENT",
        "COHESION_CONTENT_ROOT",
        "COHESION_APPLICATION_TRUST_KEY",
        "COHESION_ENDPOINT_<EP>_HOST",
        "COHESION_ENDPOINT_<EP>_PORT",
        "COHESION_ENDPOINT_<EP>_SCHEME",
        "COHESION_ENDPOINT_<EP>_PUBLIC_URL",
        "COHESION_DEPENDENCY_<RES>_<EP>_URL",
        "COHESION_DEPENDENCY_<RES>_<EP>_HOST",
        "COHESION_DEPENDENCY_<RES>_<EP>_PORT",
        "COHESION_DEPENDENCY_<RES>_<EP>_SCHEME",
        "COHESION_MOUNT_<M>_PATH",
        "COHESION_CONFIG__<Section>__<Key>",
        "COHESION_BOOTSTRAP_TOKEN_PATH",
        "COHESION_TRUST_BUNDLE_PATH",
        "COHESION_STOP_EVENT",
        "COHESION_TELEMETRY_ENDPOINT",
        "COHESION_TELEMETRY_PROTOCOL",
        "COHESION_TELEMETRY_HEADERS_PATH",
        "COHESION_LOG_FORMAT"
    ];

    [Fact(DisplayName = DisplayPrefix + "Document and ResourceEnvironment constants stay in lockstep")]
    public void VariableTable_WithResourceEnvironmentConstants_ShouldMatchExactly()
    {
        // Arrange
        string repositoryRoot = GetRepositoryRoot();
        string sourcePath = Path.Combine(
            repositoryRoot,
            "libraries",
            "Core",
            "Assimalign.Cohesion.Core",
            "src",
            "ResourceEnvironment.cs");
        string contractPath = Path.Combine(repositoryRoot, "docs", "RUNTIME_CONTRACT.md");

        // Act
        IReadOnlyList<string> sourceVariables = ReadSourceConstants(sourcePath);
        IReadOnlyList<string> documentedVariables = ReadDocumentedVariables(contractPath);

        // Assert
        sourceVariables.Count.ShouldBeGreaterThan(0);
        documentedVariables.Count.ShouldBeGreaterThan(0);
        sourceVariables.ShouldBe(ExpectedVariables);
        documentedVariables.ShouldBe(ExpectedVariables);
    }

    [Fact(DisplayName = DisplayPrefix + "Cohesion variable literals have one source of truth")]
    public void SourceTrees_WithCohesionVariableLiteralOutsideResourceEnvironment_ShouldFail()
    {
        // Arrange
        string repositoryRoot = GetRepositoryRoot();
        string resourceEnvironmentPath = Path.GetFullPath(Path.Combine(
            repositoryRoot,
            "libraries",
            "Core",
            "Assimalign.Cohesion.Core",
            "src",
            "ResourceEnvironment.cs"));
        string[] areaRoots =
        [
            Path.Combine(repositoryRoot, "libraries", "Core"),
            Path.Combine(repositoryRoot, "libraries", "Hosting"),
            Path.Combine(repositoryRoot, "libraries", "ApplicationModel")
        ];
        var violations = new List<string>();

        // Act
        foreach (string areaRoot in areaRoots)
        {
            foreach (string projectRoot in Directory.EnumerateDirectories(areaRoot))
            {
                string sourceRoot = Path.Combine(projectRoot, "src");
                if (!Directory.Exists(sourceRoot))
                {
                    continue;
                }

                foreach (string sourcePath in Directory.EnumerateFiles(sourceRoot, "*.cs", SearchOption.AllDirectories))
                {
                    string fullPath = Path.GetFullPath(sourcePath);
                    if (string.Equals(fullPath, resourceEnvironmentPath, StringComparison.OrdinalIgnoreCase)
                        || IsBuildOutput(fullPath))
                    {
                        continue;
                    }

                    string[] lines = File.ReadAllLines(fullPath);
                    for (int index = 0; index < lines.Length; index++)
                    {
                        if (lines[index].Contains("COHESION_", StringComparison.Ordinal))
                        {
                            violations.Add($"{Path.GetRelativePath(repositoryRoot, fullPath)}:{index + 1}");
                        }
                    }
                }
            }
        }

        // Assert
        violations.ShouldBeEmpty(
            "all runtime-contract variable names must be declared in ResourceEnvironment");
    }

    private static IReadOnlyList<string> ReadSourceConstants(string sourcePath)
    {
        const string declarationMarker = "public const string";
        string source = File.ReadAllText(sourcePath);
        var variables = new List<string>();
        int searchOffset = 0;

        while (true)
        {
            int declaration = source.IndexOf(declarationMarker, searchOffset, StringComparison.Ordinal);
            if (declaration < 0)
            {
                break;
            }

            int terminator = source.IndexOf(';', declaration);
            int assignment = source.IndexOf('=', declaration);
            int openingQuote = assignment < 0 ? -1 : source.IndexOf('"', assignment);
            int closingQuote = openingQuote < 0 ? -1 : source.IndexOf('"', openingQuote + 1);

            if (terminator < 0
                || assignment < 0
                || assignment > terminator
                || openingQuote < 0
                || openingQuote > terminator
                || closingQuote < 0
                || closingQuote > terminator)
            {
                throw new InvalidDataException("ResourceEnvironment contains an unreadable public string constant.");
            }

            variables.Add(source[(openingQuote + 1)..closingQuote]);
            searchOffset = terminator + 1;
        }

        return variables;
    }

    private static IReadOnlyList<string> ReadDocumentedVariables(string contractPath)
    {
        var variables = new List<string>();

        foreach (string line in File.ReadLines(contractPath))
        {
            if (!line.StartsWith("| `COHESION_", StringComparison.Ordinal))
            {
                continue;
            }

            int openingTick = line.IndexOf('`');
            int closingTick = line.IndexOf('`', openingTick + 1);
            if (openingTick >= 0 && closingTick > openingTick)
            {
                variables.Add(line[(openingTick + 1)..closingTick]);
            }
        }

        return variables;
    }

    private static bool IsBuildOutput(string path)
    {
        string directorySeparator = Path.DirectorySeparatorChar.ToString();
        string alternateSeparator = Path.AltDirectorySeparatorChar.ToString();

        return path.Contains($"{directorySeparator}bin{directorySeparator}", StringComparison.OrdinalIgnoreCase)
            || path.Contains($"{directorySeparator}obj{directorySeparator}", StringComparison.OrdinalIgnoreCase)
            || path.Contains($"{alternateSeparator}bin{alternateSeparator}", StringComparison.OrdinalIgnoreCase)
            || path.Contains($"{alternateSeparator}obj{alternateSeparator}", StringComparison.OrdinalIgnoreCase);
    }

    private static string GetRepositoryRoot([CallerFilePath] string sourcePath = "")
    {
        DirectoryInfo? directory = new FileInfo(sourcePath).Directory;

        while (directory is not null
            && !File.Exists(Path.Combine(directory.FullName, "Assimalign.Cohesion.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName
            ?? throw new DirectoryNotFoundException("Could not locate the Cohesion repository root.");
    }
}
