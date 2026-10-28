using System;
using System.Net.Http;

using Assimalign.Cohesion.Hosting.Resources;
using Assimalign.Cohesion.Web.Hosting.Resources.Internal;

namespace Assimalign.Cohesion.Web.Hosting.Resources;

/// <summary>
/// Configures the Web resource that <c>builder.AddResource(name, ...)</c> adds: where it publishes
/// its manifest, where it serves its control plane, and the format of its control-plane content.
/// </summary>
/// <remarks>
/// The resource's actions are not configured here: they come from the settings the application's
/// features registered, such as the <c>cors</c> setting <c>builder.Services.AddCors(...)</c> registers.
/// </remarks>
public sealed class WebResourceBuilder
{
    private IResourceContentSerializer _serializer = JsonResourceContentSerializer.Create(WebResourceJsonContext.Default);

    internal WebResourceBuilder(ResourceName name)
    {
        Name = name;
        ManifestEndpoint = ReadEndpoint(AppEnvironment.Variables.ManifestEndpoint);
        ControlPlaneEndpoint = ReadEndpoint(AppEnvironment.Variables.ControlPlaneEndpoint);
    }

    /// <summary>
    /// Gets the resource's name.
    /// </summary>
    public ResourceName Name { get; }

    /// <summary>
    /// Gets or sets the URL the instance publishes its manifest to; <see langword="null"/> runs the
    /// application unmanaged.
    /// </summary>
    /// <remarks>
    /// Defaults to the <see cref="AppEnvironment.Variables.ManifestEndpoint"/> variable the
    /// provisioning gateway sets.
    /// </remarks>
    public Uri? ManifestEndpoint { get; set; }

    /// <summary>
    /// Gets or sets the URL the instance serves its control plane on, a private listener separate
    /// from the application's own. Required when <see cref="ManifestEndpoint"/> is set.
    /// </summary>
    /// <remarks>
    /// Defaults to the <see cref="AppEnvironment.Variables.ControlPlaneEndpoint"/> variable the
    /// provisioning gateway sets.
    /// </remarks>
    public Uri? ControlPlaneEndpoint { get; set; }

    /// <summary>
    /// Gets or sets the serializer of the instance's control-plane content: its manifest, action
    /// inputs and outputs, state snapshots, and results.
    /// </summary>
    /// <remarks>
    /// Defaults to JSON over the resource's own source-generated context. A gateway platform that
    /// exchanges another format, or JSON written its own way, sets its own serializer, which must
    /// know <see cref="WebCorsPolicy"/>.
    /// </remarks>
    /// <exception cref="ArgumentNullException">The value is <see langword="null"/>.</exception>
    public IResourceContentSerializer Serializer
    {
        get => _serializer;
        set => _serializer = value ?? throw new ArgumentNullException(nameof(value));
    }

    /// <summary>
    /// Gets or sets the callback that creates the HTTP client the instance publishes its manifest
    /// with; <see langword="null"/> uses a client over a handler shared by the whole process.
    /// </summary>
    /// <remarks>
    /// Called once per publication; the client is disposed afterward. Return one from an HTTP client
    /// factory to share its pooled handlers: <c>() =&gt; clients.Create("gateway")</c>.
    /// </remarks>
    public Func<HttpClient>? HttpClientFactory { get; set; }

    private static Uri? ReadEndpoint(string variable)
    {
        string? value = System.Environment.GetEnvironmentVariable(variable);

        return string.IsNullOrWhiteSpace(value) ? null : new Uri(value, UriKind.Absolute);
    }
}
