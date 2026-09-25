using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

using Microsoft.Build.Framework;
using Microsoft.Build.Utilities;

namespace Assimalign.Cohesion.Sdk.Tasks;

/// <summary>
/// Validates the Cohesion SDK and .NET SDK pins in a consumer repository's nearest global.json file.
/// </summary>
public sealed class ValidateCohesionSdkPinsTask : Task
{
    private const string BaseSdkPackageId = "Assimalign.Cohesion.Sdk";
    private static readonly HashSet<string> _cohesionSdkPackageIds = new(StringComparer.OrdinalIgnoreCase)
    {
        BaseSdkPackageId,
		"Assimalign.Cohesion.Sdk.ApplicationModel",
        "Assimalign.Cohesion.Sdk.ApiManager",
        "Assimalign.Cohesion.Sdk.ConfigurationStore",
        "Assimalign.Cohesion.Sdk.Database",
        "Assimalign.Cohesion.Sdk.EmailHub",
        "Assimalign.Cohesion.Sdk.EventHub",
        "Assimalign.Cohesion.Sdk.Gateway",
        "Assimalign.Cohesion.Sdk.IdentityHub",
        "Assimalign.Cohesion.Sdk.IoTHub",
        "Assimalign.Cohesion.Sdk.LoadBalancer",
        "Assimalign.Cohesion.Sdk.LogSpace",
        "Assimalign.Cohesion.Sdk.MediaHub",
        "Assimalign.Cohesion.Sdk.MessageHub",
        "Assimalign.Cohesion.Sdk.NatGateway",
        "Assimalign.Cohesion.Sdk.NotificationHub",
        "Assimalign.Cohesion.Sdk.Rezolvr",
        "Assimalign.Cohesion.Sdk.Scheduler",
        "Assimalign.Cohesion.Sdk.SecretStore",
        "Assimalign.Cohesion.Sdk.VpnGateway",
        "Assimalign.Cohesion.Sdk.Web"
    };

    /// <summary>
    /// Gets or sets the consumer project directory from which the global.json search begins.
    /// </summary>
    [Required]
    public string ProjectDirectory { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the minimum supported .NET SDK version.
    /// </summary>
    [Required]
    public string MinimumDotNetSdkVersion { get; set; } = string.Empty;

    /// <summary>
    /// Locates and validates the nearest global.json file.
    /// </summary>
    /// <returns><see langword="true"/> when no global.json exists or every applicable pin is valid; otherwise, <see langword="false"/>.</returns>
    public override bool Execute()
    {
        string? globalJsonPath = FindGlobalJson(ProjectDirectory);
        if (globalJsonPath is null)
        {
            return true;
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(
                File.ReadAllText(globalJsonPath),
                new JsonDocumentOptions
                {
                    AllowTrailingCommas = true,
                    CommentHandling = JsonCommentHandling.Skip
                });
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                Error(globalJsonPath, "global.json must contain a JSON object at its root.");
                return false;
            }

            ValidateDotNetSdkPin(document.RootElement, globalJsonPath);
            ValidateCohesionSdkPins(document.RootElement, globalJsonPath);

            return !Log.HasLoggedErrors;
        }
        catch (JsonException exception)
        {
            Error(globalJsonPath, $"Could not parse global.json: {exception.Message}");
            return false;
        }
        catch (IOException exception)
        {
            Error(globalJsonPath, $"Could not read global.json: {exception.Message}");
            return false;
        }
        catch (UnauthorizedAccessException exception)
        {
            Error(globalJsonPath, $"Could not read global.json: {exception.Message}");
            return false;
        }
    }

    private void ValidateDotNetSdkPin(JsonElement root, string globalJsonPath)
    {
        if (!root.TryGetProperty("sdk", out JsonElement sdk) ||
            sdk.ValueKind != JsonValueKind.Object ||
            !sdk.TryGetProperty("version", out JsonElement versionElement) ||
            versionElement.ValueKind != JsonValueKind.String)
        {
            return;
        }

        string? pinnedVersion = versionElement.GetString();
        if (string.IsNullOrWhiteSpace(pinnedVersion))
        {
            return;
        }

        if (!TryParseSdkVersion(MinimumDotNetSdkVersion, out Version? minimumVersion))
        {
            Error(globalJsonPath, $"The Cohesion SDK minimum .NET SDK version '{MinimumDotNetSdkVersion}' is invalid.");
            return;
        }

        if (!TryParseSdkVersion(pinnedVersion, out Version? actualVersion))
        {
            Error(
                globalJsonPath,
                $"global.json pins .NET SDK '{pinnedVersion}', which is not a valid SDK version; expected '{MinimumDotNetSdkVersion}' or newer.");
            return;
        }

        if (actualVersion < minimumVersion)
        {
            Error(
                globalJsonPath,
                $"global.json pins .NET SDK '{pinnedVersion}', but the Cohesion SDK requires '{MinimumDotNetSdkVersion}' or newer.");
        }
    }

    private void ValidateCohesionSdkPins(JsonElement root, string globalJsonPath)
    {
        if (!root.TryGetProperty("msbuild-sdks", out JsonElement sdkPins) ||
            sdkPins.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        var pins = new List<SdkPin>();
        foreach (JsonProperty property in sdkPins.EnumerateObject())
        {
            if (!IsCohesionSdkPackageId(property.Name))
            {
                continue;
            }

            string value = property.Value.ValueKind == JsonValueKind.String
                ? property.Value.GetString() ?? string.Empty
                : property.Value.GetRawText();
            pins.Add(new SdkPin(property.Name, value));
        }

        if (pins.Count < 2)
        {
            return;
        }

        SdkPin expectedPin = pins.FirstOrDefault(pin =>
            string.Equals(pin.PackageId, BaseSdkPackageId, StringComparison.OrdinalIgnoreCase))
            ?? pins.OrderBy(pin => pin.PackageId, StringComparer.OrdinalIgnoreCase).First();
        SdkPin[] disagreements = pins
            .Where(pin => !string.Equals(pin.Version, expectedPin.Version, StringComparison.Ordinal))
            .OrderBy(pin => pin.PackageId, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (disagreements.Length == 0)
        {
            return;
        }

        string details = string.Join(
            "; ",
            disagreements.Select(pin =>
                $"{pin.PackageId}='{pin.Version}' (expected '{expectedPin.Version}')"));
        Error(
            globalJsonPath,
            $"Cohesion SDK pins must use one exact version string. Expected '{expectedPin.Version}' from {expectedPin.PackageId}. Disagreeing pins: {details}.");
    }

    private static string? FindGlobalJson(string projectDirectory)
    {
        DirectoryInfo? current = new(Path.GetFullPath(projectDirectory));
        while (current is not null)
        {
            string candidate = Path.Combine(current.FullName, "global.json");
            if (File.Exists(candidate))
            {
                return candidate;
            }

            current = current.Parent;
        }

        return null;
    }

    private static bool IsCohesionSdkPackageId(string packageId)
    {
        return _cohesionSdkPackageIds.Contains(packageId);
    }

    private static bool TryParseSdkVersion(string value, out Version? version)
    {
        int prereleaseSeparator = value.IndexOf('-');
        string numericVersion = prereleaseSeparator < 0 ? value : value[..prereleaseSeparator];
        return Version.TryParse(numericVersion, out version);
    }

    private void Error(string globalJsonPath, string message)
    {
        Log.LogError(
            subcategory: null,
            errorCode: "COHSDK002",
            helpKeyword: null,
            file: globalJsonPath,
            lineNumber: 0,
            columnNumber: 0,
            endLineNumber: 0,
            endColumnNumber: 0,
            message: message);
    }

    private sealed record SdkPin(string PackageId, string Version);
}
