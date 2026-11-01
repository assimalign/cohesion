using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace Assimalign.Cohesion.Templates.Tests;

internal static partial class TemplateRepository
{
    internal static readonly string Root = FindRoot();
    internal static readonly string ProjectRoot = Path.Combine(Root, "tooling", "templates", "Assimalign.Cohesion.Templates");
    internal static readonly string ContentRoot = Path.Combine(ProjectRoot, "src", "content");
    internal static readonly string Version = ResolveVersion();
    internal static readonly string FeedVersion = Environment.GetEnvironmentVariable("COHESION_TEMPLATES_TEST_PACKAGE_VERSION") is { Length: > 0 } version
        ? version : Version + ".local";
    internal static readonly string Feed = Path.GetFullPath(
        Environment.GetEnvironmentVariable("COHESION_TEMPLATES_TEST_FEED") is { Length: > 0 } feed
            ? feed : "_out/packages", Root);

    internal static readonly string[] Roster =
    [
        "cohesion-app", "cohesion-gateway",
        "cohesion-web", "cohesion-spa", "cohesion-database", "cohesion-secretstore",
        "cohesion-configurationstore", "cohesion-identityhub", "cohesion-rezolvr"
    ];

    internal static readonly string[] SdkDefaults =
    [
        "OutputType", "TargetFramework", "LangVersion", "EnablePreviewFeatures",
        "ImplicitUsings", "Nullable", "IsAotCompatible"
    ];

    internal static string[] SdkIds()
    {
        string module = File.ReadAllText(Path.Combine(Root, "scripts", "modules", "CohesionPackaging.psm1"));
        string block = InventoryBlock().Match(module).Groups[1].Value;
        return QuotedId().Matches(block)
            .Select(match => match.Groups[1].Value)
            .ToArray();
    }

    internal static IEnumerable<string> RequiredPackages(string template)
    {
        var required = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Assimalign.Cohesion.Sdk" };
        var families = new HashSet<string>(StringComparer.Ordinal);
        foreach (string project in Directory.EnumerateFiles(Path.Combine(ContentRoot, template), "*.csproj", SearchOption.AllDirectories))
        {
            XDocument document = XDocument.Load(project);
            string sdk = document.Root!.Attribute("Sdk")!.Value;
            required.Add(sdk);
            // A package the content references explicitly, such as a platform package, restores
            // together with its own dependency closure.
            foreach (XElement reference in document.Descendants("CohesionPackageReference"))
            {
                AddPackageClosure(reference.Attribute("Include")!.Value, required);
            }

            string area = sdk["Assimalign.Cohesion.Sdk.".Length..];
            if (area == "Gateway")
            {
                // Sdk.Gateway adds the gateway package and no framework.
                AddPackageClosure("Assimalign.Cohesion.ApplicationModel.Gateway", required);
                continue;
            }

            families.Add("Assimalign.Cohesion.App");
            families.Add("Assimalign.Cohesion.App." + area);
        }

        foreach (string family in families)
        {
            required.Add(family + ".Ref");
            required.Add(family + ".Runtime." + RuntimeInformation.RuntimeIdentifier);
        }

        // ProcessFrameworkReferences also downloads registered targeting packs to allow
        // transitive framework references. Keep this aligned with the shipped
        // registrations instead of treating an incomplete feed as a template failure.
        XDocument registrations = XDocument.Load(Path.Combine(Root, "sdks", "Assimalign.Cohesion.Sdk",
            "Targets", "Assimalign.Cohesion.Sdk.FrameworkReference.props"));
        foreach (XElement framework in registrations.Descendants("KnownFrameworkReference"))
        {
            required.Add(framework.Attribute("TargetingPackName")!.Value);
        }

        return required.Order(StringComparer.Ordinal);
    }

    internal static string? MissingFeedReason(string template)
    {
        string[] missing = RequiredPackages(template)
            .Where(id => !File.Exists(Path.Combine(Feed, $"{id}.{FeedVersion}.nupkg"))).ToArray();
        var reasons = new List<string>();
        if (missing.Length > 0)
        {
            reasons.Add($"Feed '{Feed}' lacks packages at '{FeedVersion}': {string.Join(", ", missing)}.");
        }

        string sdkPack = Path.Combine(Feed, $"Assimalign.Cohesion.Sdk.{FeedVersion}.nupkg");
        if (File.Exists(sdkPack))
        {
            using ZipArchive archive = ZipFile.OpenRead(sdkPack);
            if (archive.GetEntry("Targets/Assimalign.Cohesion.Sdk.Defaults.props") is null)
            {
                reasons.Add($"Base SDK pack '{sdkPack}' lacks Targets/Assimalign.Cohesion.Sdk.Defaults.props (requires commit 1ba8928f or later).");
            }
        }

        return reasons.Count == 0 ? null : string.Join(" ", reasons);
    }

    internal static JsonDocument ReadJson(string file) => JsonDocument.Parse(File.ReadAllText(file),
        new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip });

    // A library package's dependencies are its project's public CohesionProjectReference items.
    // An id with no repository project stays required on its own, so the feed check reports it.
    private static void AddPackageClosure(string id, ISet<string> required)
    {
        if (!required.Add(id) || FindLibraryProject(id) is not string project)
        {
            return;
        }

        foreach (XElement reference in XDocument.Load(project).Descendants("CohesionProjectReference"))
        {
            AddPackageClosure(reference.Attribute("Include")!.Value, required);
        }
    }

    // Shipped libraries sit at <libraries|resources>/<Area>/<Id>/src/<Id>.csproj.
    private static string? FindLibraryProject(string id)
    {
        foreach (string tree in new[] { "libraries", "resources" })
        {
            foreach (string area in Directory.EnumerateDirectories(Path.Combine(Root, tree)))
            {
                string project = Path.Combine(area, id, "src", id + ".csproj");
                if (File.Exists(project))
                {
                    return project;
                }
            }
        }

        return null;
    }

    private static string FindRoot()
    {
        for (DirectoryInfo? directory = new(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "build", "Targets", "Build.Version.props")))
            {
                return directory.FullName;
            }
        }

        throw new DirectoryNotFoundException("Could not locate the Cohesion repository.");
    }

    private static string ResolveVersion()
    {
        XDocument versions = XDocument.Load(Path.Combine(Root, "build", "Targets", "Build.Version.props"));
        XDocument frameworks = XDocument.Load(Path.Combine(Root, "build", "Targets", "Build.TargetFramework.props"));
        Version framework = System.Version.Parse(frameworks.Descendants("TargetFrameworkLatest").Single().Value[3..]);
        return $"{framework.Major}.{versions.Descendants("CohesionMinorVersion").Single().Value}.{versions.Descendants("CohesionPatchVersion").Single().Value}";
    }

    [GeneratedRegex(@"\$script:CohesionReleaseSdk\s*=\s*@\(([\s\S]*?)\r?\n\)")]
    private static partial Regex InventoryBlock();

    [GeneratedRegex("'([^']+)'")]
    private static partial Regex QuotedId();

}
