using System;
using System.Collections.Generic;

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
    /// Gets or sets an explicit parameter-document path. When omitted, each application loads
    /// <c>&lt;ExportDirectory&gt;/&lt;application&gt;/parameters.json</c> when that file exists.
    /// </summary>
    public string? ParameterFile { get; set; }

    /// <summary>
    /// Gets or sets the Hosting-free client seam used for SecretStore and ConfigurationStore
    /// source resolution. Defaults to the thin protocol-client implementation.
    /// </summary>
    public IGatewayStoreClient StoreClient { get; set; } = GatewayStoreClient.Instance;

    /// <summary>
    /// Gets per-kind command clients. Shipped resource kinds use their area clients
    /// by default; replace a registration to customize delivery without referencing Hosting.
    /// </summary>
    public IList<IGatewayResourceCommandClient> CommandClients { get; } =
        new List<IGatewayResourceCommandClient>
        {
            new DatabaseGatewayCommandClient(),
            new ConfigurationStoreGatewayCommandClient(),
            new IdentityHubGatewayCommandClient(),
            new RezolvrGatewayCommandClient(),
            new SecretStoreGatewayCommandClient(),
        };

    /// <summary>Gets or sets the time source used to issue credentials.</summary>
    public TimeProvider TimeProvider { get; set; } = TimeProvider.System;

    /// <summary>
    /// Gets or sets the lifetime of a resource bootstrap credential. Defaults to 24 hours and
    /// may not exceed 24 hours.
    /// </summary>
    public TimeSpan BootstrapCredentialLifetime { get; set; } = TimeSpan.FromHours(24);

    /// <summary>
    /// Gets or sets the lifetime of a developer export token. Defaults to 8 hours and may not
    /// exceed 8 hours.
    /// </summary>
    public TimeSpan DeveloperTokenLifetime { get; set; } = TimeSpan.FromHours(8);

    /// <summary>
    /// Gets or sets the optional platform-native repository for application gateway trust
    /// keys. When omitted, keys are persisted beneath the gateway's application-scoped local
    /// state directory with DataProtection at rest.
    /// </summary>
    public IGatewayTrustKeyRepository? TrustKeyRepository { get; set; }

    /// <summary>
    /// Gets or sets the optional client used by external-resource resolvers that query a peer
    /// gateway control plane. An <see cref="IAuthenticatedControlPlaneClient"/> receives the
    /// gateway-issued export credential and trusted-peer snapshot for each resolution.
    /// </summary>
    public IControlPlaneClient? ControlPlaneClient { get; set; }

    /// <summary>
    /// Gets or sets the optional factory that serves each application's gateway control plane.
    /// When omitted, the gateway continues to publish file exports only.
    /// </summary>
    public IApplicationGatewayControlPlaneFactory? ControlPlane { get; set; }

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

        if (ParameterFile is not null && string.IsNullOrWhiteSpace(ParameterFile))
        {
            throw new ArgumentException(
                "ParameterFile must not be empty when specified.",
                nameof(ParameterFile));
        }

        ArgumentNullException.ThrowIfNull(StoreClient);
        ArgumentNullException.ThrowIfNull(TimeProvider);

        ValidateLifetime(
            BootstrapCredentialLifetime,
            TimeSpan.FromHours(24),
            nameof(BootstrapCredentialLifetime));
        ValidateLifetime(
            DeveloperTokenLifetime,
            TimeSpan.FromHours(8),
            nameof(DeveloperTokenLifetime));

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

    private static void ValidateLifetime(TimeSpan value, TimeSpan maximum, string name)
    {
        if (value <= TimeSpan.Zero || value > maximum)
        {
            throw new ArgumentOutOfRangeException(
                name,
                $"{name} must be greater than zero and no longer than {maximum.TotalHours} hours.");
        }
    }
}
