using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml.Linq;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.App.Runtime.Tests;

public sealed class FrameworkManifestTests
{
    private const string AppFramework = "Assimalign.Cohesion.App";
    private const string CohesionAssemblyPrefix = "Assimalign.Cohesion.";
    private static readonly string RepositoryRoot = FindRepositoryRoot();
    private static readonly string ManifestPath = Path.Combine(
        RepositoryRoot,
        "frameworks",
        "Assimalign.Cohesion.App.props");

    [Fact(DisplayName = "Cohesion Test [Framework] - App manifest is the project-graph closure of its kernel roots")]
    public void AppManifest_KernelRoots_EqualsProjectReferenceClosure()
    {
        // Arrange
        IReadOnlyDictionary<string, ProjectNode> graph = LoadProjectGraph();
        XElement appGroup = LoadFrameworkGroups()[AppFramework];
        string[] roots = ReadItems(appGroup, "CohesionAppKernelRoot");
        string[] declaredAssemblies = ReadItems(appGroup, "CohesionFrameworkAssembly");
        string[] expectedClosure =
        [
            "Assimalign.Cohesion.App",
            "Assimalign.Cohesion.Configuration",
            "Assimalign.Cohesion.Configuration.CommandLine",
            "Assimalign.Cohesion.Configuration.EnvironmentVariables",
            "Assimalign.Cohesion.Configuration.FileSystem",
            "Assimalign.Cohesion.Configuration.Json",
            "Assimalign.Cohesion.Connections",
            "Assimalign.Cohesion.Core",
            "Assimalign.Cohesion.DependencyInjection",
            "Assimalign.Cohesion.FileSystem",
            "Assimalign.Cohesion.FileSystem.Physical",
            "Assimalign.Cohesion.Hosting",
            "Assimalign.Cohesion.Hosting.Health",
            "Assimalign.Cohesion.Hosting.Resources",
            "Assimalign.Cohesion.Hosting.Telemetry",
            "Assimalign.Cohesion.Logging",
            "Assimalign.Cohesion.Logging.Console",
            "Assimalign.Cohesion.OpenTelemetry"
        ];

        // Act
        string[] actualClosure = ComputeClosure(graph, roots)
            .Append(AppFramework)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        // Assert
        roots.Length.ShouldBe(13);
        declaredAssemblies.ShouldBe([AppFramework]);
        actualClosure.ShouldBe(expectedClosure);
    }

    [Fact(DisplayName = "Cohesion Test [Framework] - every area framework covers its shipped project closure")]
    public void AreaManifests_ShippedSurfaceClosures_AreCoveredByAppAndAreaFramework()
    {
        // Arrange
        IReadOnlyDictionary<string, ProjectNode> graph = LoadProjectGraph();
        IReadOnlyDictionary<string, XElement> groups = LoadFrameworkGroups();
        string[] kernelRoots = ReadItems(groups[AppFramework], "CohesionAppKernelRoot");
        var kernel = ComputeClosure(graph, kernelRoots)
            .Append(AppFramework)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        string[] expectedAreas =
        [
            "ApiManager", "ConfigurationStore", "Database", "EmailHub", "EventHub",
            "IdentityHub", "IoTHub", "LoadBalancer", "LogSpace", "MediaHub", "MessageHub",
            "NatGateway", "NotificationHub", "Rezolvr", "Scheduler", "SecretStore",
            "VpnGateway", "Web"
        ];
        string[] actualAreas = groups.Keys
            .Where(name => name.StartsWith(AppFramework + ".", StringComparison.Ordinal))
            .Select(name => name[(AppFramework.Length + 1)..])
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        // Act / Assert
        actualAreas.ShouldBe(expectedAreas);
        foreach (string area in actualAreas)
        {
            string frameworkName = $"{AppFramework}.{area}";
            XElement group = groups[frameworkName];
            var publicAssemblies = ReadItems(group, "CohesionFrameworkAssembly")
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var privateAssemblies = ReadItems(group, "CohesionFrameworkPrivateAssembly")
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            string hostingAssembly = $"Assimalign.Cohesion.{area}.Hosting";
            string hostingPrefix = hostingAssembly + ".";
            string[] hostingRoots = graph.Values
                .Where(project => IsSourceProject(project.ProjectPath))
                .Select(project => project.AssemblyName)
                .Where(name =>
                    string.Equals(name, hostingAssembly, StringComparison.OrdinalIgnoreCase) ||
                    name.StartsWith(hostingPrefix, StringComparison.OrdinalIgnoreCase))
                .ToArray();

            // The hosting family is mandatory. Existing manifest entries are roots too, so
            // feature assemblies such as Web.Caching cannot rely on an App eviction.
            string[] publicRoots = publicAssemblies
                .Where(graph.ContainsKey)
                .Concat(hostingRoots)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            string[] privateRoots = privateAssemblies.Where(graph.ContainsKey).ToArray();
            IReadOnlyDictionary<string, bool> closure = ComputeVisibilityClosure(
                graph,
                publicRoots,
                privateRoots);
            var covered = kernel
                .Concat(publicAssemblies)
                .Concat(privateAssemblies)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            string[] missing = closure.Keys
                .Where(name => !covered.Contains(name))
                .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            missing.ShouldBeEmpty(
                $"{frameworkName} is missing project-graph dependencies: {string.Join(", ", missing)}");
        }
    }

    private static IReadOnlyDictionary<string, ProjectNode> LoadProjectGraph()
    {
        string[] projectPaths =
        [
            .. Directory.EnumerateFiles(Path.Combine(RepositoryRoot, "libraries"), "*.csproj", SearchOption.AllDirectories),
            .. Directory.EnumerateFiles(Path.Combine(RepositoryRoot, "resources"), "*.csproj", SearchOption.AllDirectories)
        ];
        var assemblyByPath = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var documentsByPath = new Dictionary<string, XDocument>(StringComparer.OrdinalIgnoreCase);

        foreach (string projectPath in projectPaths.Where(IsIndexedProject))
        {
            string fullPath = Path.GetFullPath(projectPath);
            XDocument document = XDocument.Load(fullPath, LoadOptions.None);
            string assemblyName = document.Descendants()
                .Where(element => element.Name.LocalName == "AssemblyName")
                .Select(element => element.Value.Trim())
                .FirstOrDefault(value => value.Length != 0 && !value.Contains("$(", StringComparison.Ordinal))
                ?? Path.GetFileNameWithoutExtension(fullPath);
            assemblyByPath[fullPath] = assemblyName;
            documentsByPath[fullPath] = document;
        }

        var graph = new Dictionary<string, ProjectNode>(StringComparer.OrdinalIgnoreCase);
        foreach ((string projectPath, string assemblyName) in assemblyByPath)
        {
            XDocument document = documentsByPath[projectPath];
            var references = new List<ProjectEdge>();
            AddNamedReferences(document, references, "CohesionProjectReference", isPrivate: false);
            AddNamedReferences(document, references, "CohesionPrivateProjectReference", isPrivate: true);

            foreach (XElement element in document.Descendants().Where(
                element => element.Name.LocalName == "ProjectReference"))
            {
                string include = (string?)element.Attribute("Include") ?? string.Empty;
                if (include.Length == 0 || include.Contains("$(", StringComparison.Ordinal) ||
                    include.Contains("@(", StringComparison.Ordinal))
                {
                    continue;
                }

                string normalizedInclude = include.Replace('\\', Path.DirectorySeparatorChar);
                string referencePath = Path.GetFullPath(Path.Combine(
                    Path.GetDirectoryName(projectPath)!,
                    normalizedInclude));
                if (assemblyByPath.TryGetValue(referencePath, out string? referenceName))
                {
                    bool isPrivate = string.Equals(
                        (string?)element.Attribute("PrivateAssets"),
                        "all",
                        StringComparison.OrdinalIgnoreCase);
                    references.Add(new ProjectEdge(referenceName, isPrivate));
                }
            }

            graph[assemblyName] = new ProjectNode(assemblyName, projectPath, references);
        }

        return graph;
    }

    private static void AddNamedReferences(
        XDocument document,
        List<ProjectEdge> references,
        string itemName,
        bool isPrivate)
    {
        foreach (XElement element in document.Descendants().Where(element => element.Name.LocalName == itemName))
        {
            string include = (string?)element.Attribute("Include") ?? string.Empty;
            foreach (string name in include.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (!name.Contains("$(", StringComparison.Ordinal) && !name.Contains("@(", StringComparison.Ordinal))
                {
                    references.Add(new ProjectEdge(name, isPrivate));
                }
            }
        }
    }

    private static string[] ComputeClosure(
        IReadOnlyDictionary<string, ProjectNode> graph,
        IEnumerable<string> roots)
    {
        var closure = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var pending = new Queue<string>(roots);
        while (pending.Count != 0)
        {
            string assemblyName = pending.Dequeue();
            graph.ContainsKey(assemblyName).ShouldBeTrue(
                $"Assembly '{assemblyName}' does not resolve through the libraries/resources project index.");
            if (!closure.Add(assemblyName))
            {
                continue;
            }

            foreach (ProjectEdge edge in graph[assemblyName].References.Where(
                edge => edge.AssemblyName.StartsWith(CohesionAssemblyPrefix, StringComparison.Ordinal)))
            {
                pending.Enqueue(edge.AssemblyName);
            }
        }

        return closure.OrderBy(name => name, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static IReadOnlyDictionary<string, bool> ComputeVisibilityClosure(
        IReadOnlyDictionary<string, ProjectNode> graph,
        IEnumerable<string> publicRoots,
        IEnumerable<string> privateRoots)
    {
        var visibility = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        var pending = new Queue<(string AssemblyName, bool IsPrivate)>(
            publicRoots.Select(name => (name, false)).Concat(privateRoots.Select(name => (name, true))));

        while (pending.Count != 0)
        {
            (string assemblyName, bool isPrivate) = pending.Dequeue();
            if (!graph.TryGetValue(assemblyName, out ProjectNode? project))
            {
                continue;
            }

            if (visibility.TryGetValue(assemblyName, out bool previous) && (!previous || previous == isPrivate))
            {
                continue;
            }

            visibility[assemblyName] = isPrivate;
            foreach (ProjectEdge edge in project.References.Where(
                edge => edge.AssemblyName.StartsWith(CohesionAssemblyPrefix, StringComparison.Ordinal)))
            {
                pending.Enqueue((edge.AssemblyName, isPrivate || edge.IsPrivate));
            }
        }

        return visibility;
    }

    private static IReadOnlyDictionary<string, XElement> LoadFrameworkGroups()
    {
        XDocument manifest = XDocument.Load(ManifestPath, LoadOptions.None);
        return manifest.Root!
            .Elements()
            .Where(element => element.Name.LocalName == "ItemGroup")
            .Select(element => new
            {
                Element = element,
                Framework = ReadFrameworkCondition((string?)element.Attribute("Condition"))
            })
            .Where(entry => entry.Framework is not null)
            .ToDictionary(
                entry => entry.Framework!,
                entry => entry.Element,
                StringComparer.OrdinalIgnoreCase);
    }

    private static string? ReadFrameworkCondition(string? condition)
    {
        const string marker = "== '";
        if (condition is null || !condition.Contains("$(CohesionFrameworkName)", StringComparison.Ordinal))
        {
            return null;
        }

        int start = condition.IndexOf(marker, StringComparison.Ordinal);
        if (start < 0)
        {
            return null;
        }

        start += marker.Length;
        int end = condition.IndexOf('\'', start);
        return end < 0 ? null : condition[start..end];
    }

    private static string[] ReadItems(XElement group, string itemName)
    {
        return group.Elements()
            .Where(element => element.Name.LocalName == itemName)
            .Select(element => (string?)element.Attribute("Include"))
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value!)
            .ToArray();
    }

    private static bool IsIndexedProject(string projectPath)
    {
        return !projectPath.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase) &&
               !projectPath.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsSourceProject(string projectPath)
    {
        return projectPath.Contains(
            $"{Path.DirectorySeparatorChar}src{Path.DirectorySeparatorChar}",
            StringComparison.OrdinalIgnoreCase);
    }

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? current = new(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "frameworks", "Assimalign.Cohesion.App.props")))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        throw new DirectoryNotFoundException(
            $"Could not locate the Cohesion repository above '{AppContext.BaseDirectory}'.");
    }

    private sealed record ProjectNode(
        string AssemblyName,
        string ProjectPath,
        IReadOnlyList<ProjectEdge> References);

    private sealed record ProjectEdge(string AssemblyName, bool IsPrivate);
}
