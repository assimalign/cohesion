using System;
using System.Collections.Generic;
using System.Text.Json;

using Assimalign.Cohesion.ApplicationModel.Gateway.Internal;

namespace Assimalign.Cohesion.ApplicationModel.Gateway;

/// <summary>
/// What a gateway deploys and where: the static deployment manifest a gateway starts with,
/// generated at build time or passed in its arguments.
/// </summary>
/// <remarks>
/// The wire form is UTF-8 JSON with camel-case property names, read by <see cref="Parse(ReadOnlySpan{byte})"/>
/// and written by <see cref="ToUtf8Json"/>. Parsing checks the syntax only; <see cref="ToModel"/>
/// checks the hierarchy.
/// </remarks>
/// <param name="Gateway">The gateway the manifest is for.</param>
/// <param name="Application">
/// What to deploy: the manifest of the whole hierarchy, so dependencies outside the gateway's own
/// part resolve.
/// </param>
/// <param name="Outputs">
/// The values of outputs of resources in the gateways above, which settings in the gateway's part
/// use, as the parent gateway knew them when it deployed this one; <see langword="null"/> or empty
/// for none.
/// </param>
/// <param name="Infrastructure">
/// Where to deploy it: settings only the gateway's infrastructure provider reads, or
/// <see langword="null"/> when the provider needs none.
/// </param>
public sealed record ApplicationDeploymentManifest(
    GatewayName Gateway,
    ApplicationManifest Application,
    IReadOnlyList<ApplicationResourceOutputValue>? Outputs,
    JsonElement? Infrastructure)
{
    /// <summary>
    /// Creates the model of the gateway the manifest is for, with the output values it carries.
    /// </summary>
    /// <returns>The model.</returns>
    /// <exception cref="ArgumentNullException"><see cref="Application"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">
    /// <see cref="Application"/> breaks a hierarchy rule, or declares no gateway named <see cref="Gateway"/>;
    /// or an output value is malformed or not of a resource in a gateway above it.
    /// </exception>
    public ApplicationModel ToModel()
    {
        return ApplicationModel.Create(Application, Gateway, Outputs ?? []);
    }

    /// <summary>
    /// Writes the manifest as UTF-8 JSON.
    /// </summary>
    /// <returns>The JSON.</returns>
    public byte[] ToUtf8Json()
    {
        return JsonSerializer.SerializeToUtf8Bytes(this, GatewayJsonContext.Default.ApplicationDeploymentManifest);
    }

    /// <summary>
    /// Reads a manifest from UTF-8 JSON.
    /// </summary>
    /// <param name="utf8Json">The JSON.</param>
    /// <returns>The manifest.</returns>
    /// <exception cref="JsonException">The JSON is not a deployment manifest.</exception>
    public static ApplicationDeploymentManifest Parse(ReadOnlySpan<byte> utf8Json)
    {
        return JsonSerializer.Deserialize(utf8Json, GatewayJsonContext.Default.ApplicationDeploymentManifest)
            ?? throw new JsonException(ErrorMessages.ApplicationDeploymentManifestNull);
    }

    /// <summary>
    /// Reads a manifest from JSON text, such as a command-line argument.
    /// </summary>
    /// <param name="json">The JSON.</param>
    /// <returns>The manifest.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="json"/> is <see langword="null"/>.</exception>
    /// <exception cref="JsonException">The JSON is not a deployment manifest.</exception>
    public static ApplicationDeploymentManifest Parse(string json)
    {
        ArgumentNullException.ThrowIfNull(json);

        return JsonSerializer.Deserialize(json, GatewayJsonContext.Default.ApplicationDeploymentManifest)
            ?? throw new JsonException(ErrorMessages.ApplicationDeploymentManifestNull);
    }
}
