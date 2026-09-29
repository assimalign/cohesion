using System;

namespace Assimalign.Cohesion.Hosting.Resources;

/// <summary>
/// Defines the claim, scope, audience, and lifetime constants of the default version 1
/// application-key credential profile shared by Cohesion gateways and resources.
/// </summary>
/// <remarks>
/// <para>
/// The default resource credential is a compact ES256 JSON Web Token signed by the application
/// trust key whose public JWK a resource receives through
/// <see cref="AppEnvironment.Variables.ApplicationTrustKey"/>. The token names the application as
/// <c>iss</c>, the calling identity as <c>sub</c>, and the receiving resource (or
/// <see cref="ExportAudience"/>) as <c>aud</c>, and carries <c>exp</c>, <c>nbf</c>, <c>iat</c>, and
/// <c>jti</c>.
/// </para>
/// <para>
/// Like <see cref="AppEnvironment.Variables"/>, the string values are <see langword="const"/> and are
/// inlined into consuming assemblies: changing one is a breaking wire change. An application that
/// registers a different credential issuer registers a matching verifier on its resources instead of
/// changing these values.
/// </para>
/// </remarks>
public static class ResourceCredentialProfile
{
    /// <summary>Gets the private claim that names the intended use of a gateway-minted credential.</summary>
    public const string TokenUseClaim = "cohesion_token_use";

    /// <summary>
    /// Gets the <see cref="TokenUseClaim"/> value that lets a credential dispatch commands through a
    /// gateway control plane.
    /// </summary>
    public const string GatewayTokenUse = "gateway";

    /// <summary>Gets the claim that carries a credential's scope.</summary>
    public const string ScopeClaim = "scope";

    /// <summary>
    /// Gets the <see cref="ScopeClaim"/> value of a telemetry-emitter credential, whose audience is the
    /// telemetry sink and whose subject is the emitting resource.
    /// </summary>
    public const string TelemetryScope = "telemetry";

    /// <summary>Gets the audience of a credential presented to a gateway control plane.</summary>
    public const string ExportAudience = "cohesion-export";

    /// <summary>
    /// Gets the clock skew applied to the temporal claims of an application-key credential.
    /// </summary>
    public static TimeSpan ClockSkew { get; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Gets the maximum <c>exp</c> minus <c>iat</c> of a bootstrap, resource-access, or telemetry
    /// credential presented to a resource.
    /// </summary>
    public static TimeSpan BootstrapMaximumLifetime { get; } = TimeSpan.FromHours(24);

    /// <summary>
    /// Gets the maximum <c>exp</c> minus <c>iat</c> of a developer or peer credential presented to a
    /// gateway control plane with the <see cref="ExportAudience"/> audience.
    /// </summary>
    public static TimeSpan DeveloperMaximumLifetime { get; } = TimeSpan.FromHours(8);
}
