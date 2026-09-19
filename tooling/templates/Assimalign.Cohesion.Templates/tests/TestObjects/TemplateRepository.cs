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
        "cohesion-app", "cohesion-landing-zone", "cohesion-gateway", "cohesion-composite",
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
        string module = File.ReadAllText(Path.Combine(Root, "installer", "scripts", "modules", "CohesionPackaging.psm1"));
        string block = InventoryBlock().Match(module).Groups[1].Value;
        return QuotedId().Matches(block).Select(match => match.Groups[1].Value).ToArray();
    }

    internal static IEnumerable<string> RequiredPackages(string template)
    {
        var required = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Assimalign.Cohesion.Sdk" };
        var families = new HashSet<string>(StringComparer.Ordinal) { "Assimalign.Cohesion.App" };
        bool gateway = false;
        foreach (string project in Directory.EnumerateFiles(Path.Combine(ContentRoot, template), "*.csproj", SearchOption.AllDirectories))
        {
            XDocument document = XDocument.Load(project);
            string sdk = document.Root!.Attribute("Sdk")!.Value;
            required.Add(sdk);
            string area = sdk["Assimalign.Cohesion.Sdk.".Length..];
            if (area == "Gateway")
            {
                gateway = true;
                if (document.Descendants("CohesionGatewayInProcess").SingleOrDefault()?.Value == "true")
                {
                    // Sdk.Gateway adds these framework references when InProcess is active,
                    // including for an otherwise empty gateway/composite template.
                    families.Add("Assimalign.Cohesion.App.Web");
                    families.Add("Assimalign.Cohesion.App.Database");
                }
                continue;
            }

            families.Add("Assimalign.Cohesion.App." + area);
            if (document.Descendants("CohesionApplicationModel").Single().Value == "enabled")
            {
                // Read the SDK's owner rather than assuming a package name from the area.
                XDocument props = XDocument.Load(Path.Combine(Root, "sdks", sdk, "Targets", $"Sdk.{area}.props"));
                required.Add(props.Descendants("CohesionResourceApplicationModel").Single().Value);
            }
        }

        if (gateway)
        {
            // The SDK suite is the reference package closure; its SDK-only list is not needed here.
            string consumer = File.ReadAllText(Path.Combine(Root, "sdks", "Assimalign.Cohesion.Sdk.Gateway", "Tasks", "tests", "ConsumerWorkspace.cs"));
            string block = GatewayClosure().Match(consumer).Groups[1].Value;
            foreach (Match match in LibraryId().Matches(block))
            {
                required.Add(match.Groups[1].Value);
            }
        }

        foreach (string family in families)
        {
            required.Add(family + ".Ref");
            required.Add(family + ".Runtime." + RuntimeInformation.RuntimeIdentifier);
        }

        // ProcessFrameworkReferences also downloads registered targeting packs to allow
        // transitive framework references. Gateway builds supply a RID, so their restore
        // requests the registered runtime packs too. Keep this aligned with the shipped
        // registrations instead of treating an incomplete feed as a template failure.
        XDocument registrations = XDocument.Load(Path.Combine(Root, "sdks", "Assimalign.Cohesion.Sdk",
            "Targets", "Assimalign.Cohesion.Sdk.FrameworkReference.props"));
        foreach (XElement framework in registrations.Descendants("KnownFrameworkReference"))
        {
            required.Add(framework.Attribute("TargetingPackName")!.Value);
            if (gateway)
            {
                required.Add(framework.Attribute("RuntimePackNamePatterns")!.Value
                    .Replace("**RID**", RuntimeInformation.RuntimeIdentifier, StringComparison.Ordinal));
            }
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

    [GeneratedRegex(@"RequiredPackageIds\s*=\s*\[([\s\S]*?)\];")]
    private static partial Regex GatewayClosure();

    [GeneratedRegex("\"(Assimalign.Cohesion.[^\"]+)\"")]
    private static partial Regex LibraryId();
}
