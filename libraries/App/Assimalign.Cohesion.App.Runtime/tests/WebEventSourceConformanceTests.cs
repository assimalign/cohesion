using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Diagnostics.Tracing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Xml.Linq;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.App.Runtime.Tests;

[Collection(nameof(WebEventSourceCollection))]
public sealed class WebEventSourceConformanceTests
{
    private const string WebAssembly = "Assimalign.Cohesion.Web";

    [Fact(DisplayName = "Cohesion Test [Framework] - App.Web members: Event sources conform to their assembly and documented policy")]
    [UnconditionalSuppressMessage("Trimming", "IL2026", Justification = "Test-only discovery and strict manifest validation of the complete, untrimmed framework membership.")]
    [UnconditionalSuppressMessage("Trimming", "IL2072", Justification = "Test-only runtime type discovery in an untrimmed test assembly; all declared source methods are present for manifest validation.")]
    [UnconditionalSuppressMessage("Trimming", "IL2111", Justification = "Test-only strict manifest validation; no reflection ships in a framework assembly.")]
    public void GetTypes_AppWebMembers_ShouldConformToEventSourcePolicy()
    {
        // Arrange
        string repositoryRoot = FindRepositoryRoot();
        string[] members = ReadMemberNames(repositoryRoot);
        IReadOnlyDictionary<string, ProjectInfo> projects = ReadProjects(repositoryRoot, members);
        string[] catalog = File.ReadAllLines(Path.Combine(repositoryRoot, "docs", "EVENT_SOURCES.md"));
        string[] documentedSources = ReadCatalogNames(catalog, "## Cohesion event sources");
        string[] uninstrumented = ReadCatalogNames(catalog, "### Web assemblies deliberately left uninstrumented");
        var observedWebSources = new List<string>();

        // These members must never disappear behind a prefix filter or a transport exclusion.
        members.ShouldContain("Assimalign.Cohesion.Web.Authentication.Certificate");
        members.ShouldContain("Assimalign.Cohesion.Http.Connections");
        members.ShouldContain("Assimalign.Cohesion.Web.Hosting");
        members.ShouldContain("Assimalign.Cohesion.Web.Server");
        members.ShouldNotContain("Assimalign.Cohesion.Web.Hosting.Health");
        members.ShouldNotContain("Assimalign.Cohesion.Web.Hosting.Resources");
        documentedSources.Distinct(StringComparer.Ordinal).Count().ShouldBe(documentedSources.Length,
            "The event-source catalog must have one row per source.");
        uninstrumented.ShouldNotBeEmpty("The catalog must declare its deliberate no-source policy.");
        uninstrumented.Distinct(StringComparer.Ordinal).Count().ShouldBe(uninstrumented.Length);

        // Act / Assert - load every declared public or private member, including non-Web assemblies.
        // Http.Connections is inspected whether or not its source has landed in this checkout.
        foreach (string assemblyName in members)
        {
            projects.ContainsKey(assemblyName).ShouldBeTrue($"No source project was found for {assemblyName}.");
            ProjectInfo project = projects[assemblyName];
            Assembly assembly = Assembly.Load(new AssemblyName(assemblyName));
            assembly.GetName().Name.ShouldBe(assemblyName);
            Type[] sources = assembly.GetTypes()
                .Where(type => type.IsSubclassOf(typeof(EventSource)))
                .ToArray();
            sources.Length.ShouldBeLessThanOrEqualTo(1, $"{assemblyName} may own at most one EventSource.");

            bool deliberatelyUninstrumented = uninstrumented.Contains(assemblyName, StringComparer.Ordinal) ||
                uninstrumented.Contains(project.ProjectName, StringComparer.Ordinal);
            if (deliberatelyUninstrumented)
            {
                sources.ShouldBeEmpty($"{assemblyName} is documented as deliberately uninstrumented.");
            }

            if (IsWebAssembly(assemblyName))
            {
                (deliberatelyUninstrumented || documentedSources.Contains(assemblyName, StringComparer.Ordinal))
                    .ShouldBeTrue($"{assemblyName} must have a source row or a deliberate no-source explanation.");
            }

            foreach (Type source in sources)
            {
                source.IsNotPublic.ShouldBeTrue($"{source.FullName} must be internal and top-level.");
                source.IsSealed.ShouldBeTrue($"{source.FullName} must be sealed.");
                source.BaseType.ShouldBe(typeof(EventSource));
                source.Namespace.ShouldBe(project.RootNamespace + ".Internal");
                EventSource.GetName(source).ShouldBe(assemblyName);
                string? manifest = EventSource.GenerateManifest(source, null, EventManifestOptions.Strict);
                manifest.ShouldNotBeNullOrEmpty($"{source.FullName} must have a valid strict manifest.");
                if (assemblyName == "Assimalign.Cohesion.Web.Hosting")
                {
                    source.Name.ShouldBe("WebHostingEventSource");
                    XDocument.Parse(manifest).Descendants()
                        .Where(element => element.Name.LocalName == "event" &&
                            (string?)element.Attribute("symbol") is "ControlPlaneDisabled" or "CredentialRejected" or "CommandMalformed" or "StopAccepted")
                        .Select(element => (element.Attribute("symbol")!.Value, (int)element.Attribute("value")!))
                        .OrderBy(item => item.Item2).ToArray()
                        .ShouldBe(new[] { ("ControlPlaneDisabled", 1), ("CredentialRejected", 2), ("CommandMalformed", 3), ("StopAccepted", 4) });
                }

                if (assemblyName == "Assimalign.Cohesion.Web.Server")
                {
                    source.Name.ShouldBe("WebServerEventSource");
                    XDocument.Parse(manifest).Descendants()
                        .Where(element => element.Name.LocalName == "event" &&
                            (int)element.Attribute("value")! > 0)
                        .Select(element => (int)element.Attribute("value")!)
                        .OrderBy(id => id).ToArray().ShouldBe(Enumerable.Range(1, 14).ToArray());
                }

                if (IsWebAssembly(assemblyName))
                {
                    observedWebSources.Add(assemblyName);
                    File.Exists(project.DesignPath).ShouldBeTrue($"{assemblyName} must have a DESIGN.md.");
                    File.ReadLines(project.DesignPath)
                        .Any(line => line.StartsWith("## Diagnostics", StringComparison.Ordinal))
                        .ShouldBeTrue($"{project.DesignPath} must document its diagnostics.");
                }
            }
        }

        observedWebSources.ShouldNotBeEmpty();
        observedWebSources.OrderBy(name => name, StringComparer.Ordinal).ToArray().ShouldBe(
            documentedSources.Where(IsWebAssembly).OrderBy(name => name, StringComparer.Ordinal).ToArray(),
            "The catalog must document exactly the Web sources present in App.Web; its count is discovered, not fixed.");
    }

    private static string[] ReadMemberNames(string repositoryRoot)
    {
        XDocument manifest = XDocument.Load(Path.Combine(repositoryRoot, "resources", "Web",
            "Assimalign.Cohesion.Web.Runtime", "Directory.Build.props"));
        string[] members = manifest.Descendants()
            .Where(element => element.Name.LocalName is "CohesionFrameworkAssembly" or "CohesionFrameworkPrivateAssembly")
            .SelectMany(element => ((string?)element.Attribute("Include") ?? string.Empty)
                .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .ToArray();
        members.ShouldNotBeEmpty();
        members.Distinct(StringComparer.Ordinal).Count().ShouldBe(members.Length);
        members.ShouldAllBe(name => !name.Contains("$(", StringComparison.Ordinal) && !name.Contains("@(", StringComparison.Ordinal),
            "The membership guard needs concrete assembly names from the Runtime producer's manifest.");
        return members;
    }

    private static IReadOnlyDictionary<string, ProjectInfo> ReadProjects(string repositoryRoot, string[] members)
    {
        var projects = new Dictionary<string, ProjectInfo>(StringComparer.Ordinal);
        var memberNames = new HashSet<string>(members, StringComparer.Ordinal);
        foreach (string area in new[] { "libraries", "resources" })
        {
            foreach (string path in Directory.EnumerateFiles(Path.Combine(repositoryRoot, area), "*.csproj", SearchOption.AllDirectories)
                .Where(IsSourceProject))
            {
                XDocument document = XDocument.Load(path);
                string projectName = Path.GetFileNameWithoutExtension(path);
                string assemblyName = ReadProperty(document, "AssemblyName") ?? projectName;
                if (!memberNames.Contains(assemblyName))
                {
                    continue;
                }

                string rootNamespace = ReadProperty(document, "RootNamespace") ?? projectName;
                string projectDirectory = Path.GetDirectoryName(Path.GetDirectoryName(path))!;
                projects.Add(assemblyName, new ProjectInfo(projectName, rootNamespace,
                    Path.Combine(projectDirectory, "docs", "DESIGN.md")));
            }
        }

        return projects;
    }

    private static string? ReadProperty(XDocument document, string name)
        => document.Descendants()
            .Where(element => element.Name.LocalName == name)
            .Select(element => element.Value.Trim())
            .FirstOrDefault(value => value.Length != 0 && !value.Contains("$(", StringComparison.Ordinal));

    private static bool IsSourceProject(string path)
    {
        string[] segments = path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return segments[^2] == "src" && !segments.Any(segment =>
            segment is "tests" or "examples" or "samples" or "bin" or "obj");
    }

    private static string[] ReadCatalogNames(string[] lines, string heading)
    {
        int start = Array.IndexOf(lines, heading);
        start.ShouldBeGreaterThanOrEqualTo(0, $"The event-source catalog is missing '{heading}'.");
        return lines.Skip(start + 1)
            .TakeWhile(line => !line.StartsWith('#'))
            .Where(line => line.StartsWith("| `", StringComparison.Ordinal))
            .SelectMany(line => line.Split('|')[1].Split('`')
                .Where((_, index) => index % 2 == 1))
            .Where(name => name.StartsWith("Assimalign.Cohesion.", StringComparison.Ordinal))
            .ToArray();
    }

    private static bool IsWebAssembly(string name)
        => name == WebAssembly || name.StartsWith(WebAssembly + ".", StringComparison.Ordinal);

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "libraries", "App", "Assimalign.Cohesion.App.props")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException($"Could not locate the Cohesion repository above '{AppContext.BaseDirectory}'.");
    }

    private sealed record ProjectInfo(string ProjectName, string RootNamespace, string DesignPath);
}
