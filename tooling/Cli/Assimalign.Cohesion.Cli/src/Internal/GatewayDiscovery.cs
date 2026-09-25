using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml.Linq;

namespace Assimalign.Cohesion.Cli.Internal;

internal static class GatewayDiscovery
{
    private const string gatewaySdk = "Assimalign.Cohesion.Sdk.Gateway";

    internal static string ResolveProject(string workingDirectory, string? selected)
    {
        string root = selected is null ? workingDirectory : Path.GetFullPath(selected, workingDirectory);
        if (File.Exists(root) && Path.GetExtension(root).Equals(".csproj", StringComparison.OrdinalIgnoreCase))
        {
            return Path.GetFullPath(root);
        }
        if (!Directory.Exists(root))
        {
            throw new CliException("Project path must identify an existing csproj or directory.");
        }
        string[] candidates = FindProjects(root).Where(IsGateway).Order(StringComparer.Ordinal).ToArray();
        return candidates.Length == 1 ? candidates[0] : throw new CliException(
            $"Expected one Cohesion gateway project under {root}; found {candidates.Length}. Candidates: " +
            (candidates.Length == 0 ? "(none)" : string.Join(", ", candidates)) + ". Select one with --project.");
    }

    private static IEnumerable<string> FindProjects(string root)
    {
        foreach (string project in Directory.EnumerateFiles(root, "*.csproj"))
        {
            yield return Path.GetFullPath(project);
        }
        foreach (string directory in Directory.EnumerateDirectories(root))
        {
            string name = Path.GetFileName(directory);
            if (name.Equals("bin", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("obj", StringComparison.OrdinalIgnoreCase) ||
                (File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
            {
                continue;
            }
            foreach (string project in FindProjects(directory))
            {
                yield return project;
            }
        }
    }

    private static bool IsGateway(string path)
    {
        XElement? root = XDocument.Load(path).Root;
        if (root is null || root.Name.LocalName != "Project")
        {
            return false;
        }
        string? sdk = (string?)root.Attribute("Sdk");
        return (sdk?.Split(';').Any(IsGatewayName) ?? false) ||
            root.Elements().Any(element => element.Name.LocalName == "Sdk" &&
                IsGatewayName((string?)element.Attribute("Name")));
    }

    private static bool IsGatewayName(string? name) =>
        string.Equals(name?.Split('/')[0].Trim(), gatewaySdk, StringComparison.OrdinalIgnoreCase);

    internal static string GetStateRoot(string project, string? selected, string workingDirectory) =>
        selected is null ? Path.Combine(Path.GetDirectoryName(project)!, ".cohesion")
            : Path.GetFullPath(selected, workingDirectory);

    internal static string ResolveApplication(string project, string stateRoot, string? selected)
    {
        if (selected is not null)
        {
            return PathSegment(selected);
        }
        XDocument document = XDocument.Load(project);
        string? app = Property(document, "CohesionApplicationName") ?? Property(document, "CohesionApplication");
        if (app is not null)
        {
            return PathSegment(app);
        }
        for (DirectoryInfo? directory = new(Path.GetDirectoryName(project)!); directory is not null; directory = directory.Parent)
        {
            string props = Path.Combine(directory.FullName, "Directory.Build.props");
            if (File.Exists(props))
            {
                app = Property(XDocument.Load(props), "CohesionApplication");
                if (app is not null)
                {
                    return PathSegment(app);
                }
                // MSBuild imports the nearest props. Do not guess at an unevaluated import chain.
                break;
            }
        }
        string[] directories = Directory.Exists(stateRoot) ? Directory.GetDirectories(stateRoot) : [];
        return directories.Length switch
        {
            1 => PathSegment(Path.GetFileName(directories[0])),
            0 => throw new CliException("no local gateway state; run `cohesion run` first (or select --app for parameter set)."),
            _ => throw new CliException("Several applications have local state; select one with --app.")
        };
    }

    private static string? Property(XDocument document, string name) =>
        document.Root?.Elements().Where(element => element.Name.LocalName == "PropertyGroup")
            .SelectMany(element => element.Elements()).LastOrDefault(element =>
                element.Name.LocalName == name && !string.IsNullOrWhiteSpace(element.Value))?.Value.Trim();

    internal static string PathSegment(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value is "." or ".." ||
            value.IndexOfAny(['/', '\\', ':']) >= 0 || value.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
            value.Contains("$(", StringComparison.Ordinal) || value.EndsWith('.') || value.EndsWith(' '))
        {
            throw new CliException("Application and resource names must be literal directory names; use --app for an MSBuild expression.");
        }
        return value;
    }
}
