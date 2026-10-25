using System;
using System.Collections.Generic;

namespace Assimalign.Cohesion.ApplicationModel.Gateway;

/// <summary>
/// Common options for plan-driven application gateways.
/// </summary>
/// <remarks>
/// Stores, the certificate authority, the trusted-issuer store, command-input resolvers, and the
/// telemetry sink are not gateway options: each application registers them in
/// <see cref="IApplicationBuilder.Providers"/> for an application built in code, or in
/// <see cref="IApplicationProviderBuilder.Providers"/> for an application-set member through its
/// <c>AddApplication(..., configure)</c> callback — and the gateway resolves
/// them from <see cref="IApplicationModel.Providers"/>.
/// </remarks>
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
    /// Gets the command clients that deliver declarative commands to realized resources.
    /// </summary>
    /// <remarks>
    /// Defaults to a single <see cref="ResourceControlPlaneCommandClient"/>, which serves every
    /// resource kind through the standard control-plane <c>commands</c> route. For each delivery the
    /// gateway uses the first client whose <see cref="IGatewayResourceCommandClient.ResourceKind"/>
    /// equals the target's manifest kind and, only when none matches, the first client declaring
    /// <see cref="IGatewayResourceCommandClient.AnyKind"/>. Add an exact-kind client to customize one
    /// kind's delivery without referencing its Hosting package. Clients are not consulted for a
    /// target reached through a registered in-process control plane or a peer gateway; with no
    /// matching client, any other delivery is rejected.
    /// </remarks>
    public IList<IGatewayResourceCommandClient> CommandClients { get; } =
        new List<IGatewayResourceCommandClient>
        {
            new ResourceControlPlaneCommandClient(),
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

    /// <summary>Validates the common gateway settings before a derived options type validates its platform settings.</summary>
    /// <exception cref="ArgumentNullException">
    /// <see cref="ApplicationVersion"/> or <see cref="TimeProvider"/> is null.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// A configured directory or parameter path is empty, the application version is empty,
    /// or a controller registration is null.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// A credential lifetime is nonpositive or exceeds its maximum, or the readiness budget is nonpositive.
    /// </exception>
    protected internal void ValidateCommon()
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
