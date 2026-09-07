using System;
using System.Collections.Generic;
using System.Text.Json;

namespace Assimalign.Cohesion.ApplicationModel.Gateway;

/// <summary>
/// Common options for plan-driven application gateways.
/// </summary>
public class ApplicationGatewayOptions
{
    /// <summary>
    /// Gets or sets the root directory where application export documents are written.
    /// Defaults to <c>.cohesion</c> under the current working directory.
    /// </summary>
    public string? ExportDirectory { get; set; }

    /// <summary>
    /// Gets or sets the application version published in export documents. Defaults to
    /// <c>1</c> when the composition root does not provide a release version.
    /// </summary>
    public string ApplicationVersion { get; set; } = "1";

    /// <summary>
    /// Gets or sets the application's public JSON Web Key published in export documents.
    /// Private key material must never be assigned to this property.
    /// </summary>
    public JsonElement? TrustKey { get; set; }

    /// <summary>
    /// Gets or sets the optional client used by external-resource resolvers that query a peer
    /// gateway control plane.
    /// </summary>
    public IControlPlaneClient? ControlPlaneClient { get; set; }

    /// <summary>
    /// Gets domain-authored controller overrides. Controllers are consulted in registration
    /// order before the platform gateway's built-in plan controller. The framework registers
    /// no overrides by default.
    /// </summary>
    public IList<IApplicationResourceController> Controllers { get; } =
        new List<IApplicationResourceController>();

    /// <summary>
    /// Gets parameter values available to the default <c>parameter:</c> mount-source
    /// resolver. Values are encoded as UTF-8 for file delivery.
    /// </summary>
    public IDictionary<string, string> Parameters { get; } =
        new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>
    /// Gets or sets the readiness budget applied independently to each resource. Defaults
    /// to 60&#160;seconds.
    /// </summary>
    public TimeSpan ReadinessBudget { get; set; } = TimeSpan.FromSeconds(60);

    internal void ValidateCommon()
    {
        if (ExportDirectory is not null && string.IsNullOrWhiteSpace(ExportDirectory))
        {
            throw new ArgumentException(
                "ExportDirectory must not be empty when specified.",
                nameof(ExportDirectory));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(ApplicationVersion);

        if (TrustKey is JsonElement trustKey && trustKey.ValueKind != JsonValueKind.Object)
        {
            throw new ArgumentException(
                "TrustKey must be a public JSON Web Key object when specified.",
                nameof(TrustKey));
        }

        if (ReadinessBudget <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(ReadinessBudget),
                "ReadinessBudget must be greater than zero.");
        }

        for (int index = 0; index < Controllers.Count; index++)
        {
            if (Controllers[index] is null)
            {
                throw new ArgumentException(
                    $"Controller registration at index {index} is null.",
                    nameof(Controllers));
            }
        }
    }
}
