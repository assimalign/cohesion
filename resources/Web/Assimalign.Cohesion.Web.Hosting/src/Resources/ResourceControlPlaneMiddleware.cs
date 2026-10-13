using System;
using System.Buffers;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Hosting;
using Assimalign.Cohesion.Hosting.Health;
using Assimalign.Cohesion.Hosting.Resources;
using Assimalign.Cohesion.Http;
using Assimalign.Cohesion.Web.Hosting.Internal;

using IPAddress = System.Net.IPAddress;
using IPEndPoint = System.Net.IPEndPoint;

namespace Assimalign.Cohesion.Web.Hosting.Resources;

/// <summary>Composes the resource management and probe protocol into a Web pipeline.</summary>
// Deviates from the repo interface-first rule per O35: hosts need one static composition
// entry shared with UseResourceControlPlane, without a runtime-module dependency.
public static class ResourceControlPlaneMiddleware
{
    // Parsed trust is cached per invocation. Cryptographic handles are retained only while an
    // explicit lifetime lease exists; the weak table itself owns no unmanaged lifetime.
    private static readonly ConditionalWeakTable<ResourceContext, Lazy<BootstrapTokenVerifier>> _verifiers = new();

    private static BootstrapTokenVerifier GetVerifier(ResourceContext resource) =>
        _verifiers.GetValue(resource, static context => new Lazy<BootstrapTokenVerifier>(
            () => new BootstrapTokenVerifier(context), LazyThreadSafetyMode.ExecutionAndPublication)).Value;

    /// <summary>Retains the resource's bootstrap verification handle until the returned owner is disposed.</summary>
    /// <param name="resourceContext">The resource identity and application trust key.</param>
    /// <returns>An idempotent lifetime lease. Dispose it after the owning pipeline's requests drain.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="resourceContext"/> is null.</exception>
    /// <exception cref="InvalidOperationException">A managed resource lacks a valid identity or public trust key.</exception>
    /// <remarks>
    /// Concurrent owners for the same context share one handle; the last disposal releases it.
    /// Manual hosts retain this lease across calls to <see cref="InvokeAsync"/>. Calls without a
    /// lease remain supported and use a temporary handle per validation, with no unmanaged cache.
    /// </remarks>
    public static IDisposable CreateLifetime(ResourceContext resourceContext)
    {
        Validate(resourceContext);
        return new ResourceControlPlaneLifetime(resourceContext.GatewayName is null ? null : GetVerifier(resourceContext));
    }

    private const string HealthJsonContentType = "application/health+json; charset=utf-8";
    private const string HealthPath = "/healthz";
    private const string CohesionHealthPath = "/cohesion/v1/healthz";
    private const string ReadinessPath = "/readyz";
    private const string CohesionReadinessPath = "/cohesion/v1/readyz";
    private const string LivenessPath = "/livez";
    private const string CohesionLivenessPath = "/cohesion/v1/livez";
    private const string EndpointsPath = "/cohesion/v1/endpoints";
    private const string StopPath = "/cohesion/v1/stop";
    private const string CommandsPath = "/cohesion/v1/commands";
    private const string HostReadinessContributionName = "cohesion.host";

    /// <summary>Serves a control-plane route or forwards the request to the next middleware.</summary>
    /// <param name="controlPlane">The resource's registered control plane.</param>
    /// <param name="resourceContext">The ambient resource identity and application trust key.</param>
    /// <param name="isApplicationReady">Whether the owning application has completed startup.</param>
    /// <param name="controlPlanePort">The listener port to serve, or null to serve on every listener.</param>
    /// <param name="context">The current HTTP exchange.</param>
    /// <param name="next">The middleware to invoke for other routes or listener ports.</param>
    /// <returns>A task representing request handling.</returns>
    /// <exception cref="ArgumentNullException">A required argument is null.</exception>
    /// <remarks>
    /// Call <see cref="Validate"/> during composition. A non-null port gates every protocol path,
    /// including bare probes. Namespaced paths of a gateway-managed resource consult the credential
    /// verifier registered for the resource (<see cref="ResourceRuntime.TryGetCredentialVerifier"/>)
    /// before the default application-key verification, and admit only this application's gateway.
    /// Without a gateway identity, namespaced paths admit only loopback transport peers and refuse
    /// other or unknown peers with 403. Bare probes remain public; forwarded headers grant no access.
    /// Stop is deferred until response completion when the server supplies
    /// <see cref="IWebResponseCompletionFeature"/>; custom servers without it use direct stop.
    /// </remarks>
    public static async Task InvokeAsync(
        IResourceControlPlane controlPlane,
        ResourceContext resourceContext,
        bool isApplicationReady,
        int? controlPlanePort,
        IHttpContext context,
        WebApplicationMiddleware next)
    {
        ArgumentNullException.ThrowIfNull(controlPlane);
        ArgumentNullException.ThrowIfNull(resourceContext);
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(next);
        if (controlPlanePort is int port && context.ConnectionInfo.LocalPort != port)
        {
            await next.Invoke(context).ConfigureAwait(false);
            return;
        }

        string path = context.Request.Path.Value;
        bool isRead = context.Request.Method == HttpMethod.Get || context.Request.Method == HttpMethod.Head;

        bool isNamespacedControlPlanePath = IsNamespacedControlPlanePath(path);
        if (isNamespacedControlPlanePath && resourceContext.GatewayName is null &&
            !IsLoopbackPeer(context))
        {
            context.Response.StatusCode = HttpStatusCode.Forbidden;
            WebHostingEventSource.Log.CredentialRejected(resourceContext, context,
                ResourceCredentialStatus.Forbidden, CredentialRejectionReason.NonLoopbackPeer);
            return;
        }
        if (isNamespacedControlPlanePath && resourceContext.GatewayName is not null)
        {
            ControlPlaneCredentialVerification authorization = await AuthorizeAsync(context, resourceContext).ConfigureAwait(false);
            ResourceCredentialStatus status = authorization.Verification.Status;
            if (status is not ResourceCredentialStatus.Authorized)
            {
                context.Response.StatusCode = status is ResourceCredentialStatus.Forbidden
                    ? HttpStatusCode.Forbidden
                    : HttpStatusCode.Unauthorized;
                if (status is ResourceCredentialStatus.Unauthorized)
                {
                    context.Response.Headers[HttpHeaderKey.WWWAuthenticate] = "Bearer";
                }
                WebHostingEventSource.Log.CredentialRejected(resourceContext, context, status, authorization.Reason);
                return;
            }
        }
        if (path == HealthPath || path == CohesionHealthPath)
        {
            if (!isRead)
            {
                SetMethodNotAllowed(context, "GET, HEAD");
                return;
            }

            await WriteHealthAsync(
                context,
                await controlPlane.CheckHealthAsync(context.RequestCancelled).ConfigureAwait(false));
            return;
        }

        if (path == ReadinessPath || path == CohesionReadinessPath)
        {
            if (!isRead)
            {
                SetMethodNotAllowed(context, "GET, HEAD");
                return;
            }

            ResourceHealthReport report =
                await controlPlane.CheckReadinessAsync(context.RequestCancelled).ConfigureAwait(false);
            await WriteHealthAsync(
                context,
                isApplicationReady ? report : MarkHostAsStarting(report));
            return;
        }

        if (path == LivenessPath || path == CohesionLivenessPath)
        {
            if (!isRead)
            {
                SetMethodNotAllowed(context, "GET, HEAD");
                return;
            }

            await WriteHealthAsync(
                context,
                await controlPlane.CheckLivenessAsync(context.RequestCancelled).ConfigureAwait(false));
            return;
        }

        if (path == EndpointsPath)
        {
            if (!isRead)
            {
                SetMethodNotAllowed(context, "GET, HEAD");
                return;
            }

            await WriteJsonAsync(context, writer =>
            {
                writer.WriteStartObject();
                writer.WritePropertyName("endpoints");
                writer.WriteStartObject();
                foreach ((string name, Uri endpoint) in
                    controlPlane.ObservedEndpoints.OrderBy(static pair => pair.Key, StringComparer.Ordinal))
                {
                    writer.WriteString(name, endpoint.ToEndpointString());
                }
                writer.WriteEndObject();
                writer.WriteEndObject();
            }).ConfigureAwait(false);
            return;
        }

        if (path == StopPath)
        {
            if (context.Request.Method != HttpMethod.Post)
            {
                SetMethodNotAllowed(context, "POST");
                return;
            }

            IWebResponseCompletionFeature? responseCompletion =
                context.Features.Get<IWebResponseCompletionFeature>();
            if (responseCompletion is not null)
            {
                responseCompletion.Register(() => controlPlane.RequestStopAsync(CancellationToken.None));
            }
            else
            {
                // A custom server may omit the completion feature. Preserve direct-stop
                // behavior there; the default server defers until the acknowledgement is sent.
                await controlPlane.RequestStopAsync(context.RequestCancelled).ConfigureAwait(false);
            }
            context.Response.StatusCode = HttpStatusCode.Accepted;
            WebHostingEventSource.Log.StopAccepted(resourceContext, context, responseCompletion is not null);
            return;
        }

        if (path == CommandsPath)
        {
            if (isRead)
            {
                await WriteJsonAsync(context, writer =>
                {
                    writer.WriteStartObject();
                    writer.WritePropertyName("acceptedCommandKinds");
                    writer.WriteStartArray();
                    foreach (string kind in controlPlane.AcceptedCommandKinds)
                    {
                        writer.WriteStringValue(kind);
                    }
                    writer.WriteEndArray();
                    writer.WritePropertyName("commands");
                    writer.WriteStartArray();
                    foreach (ResourceCommand command in controlPlane.Commands)
                    {
                        writer.WriteStartObject();
                        writer.WriteString("id", command.Id);
                        writer.WriteString("kind", command.Kind);
                        writer.WriteString("owner", command.Owner);
                        writer.WriteString("key", command.Key);
                        writer.WriteString("status", "Applied");
                        writer.WriteEndObject();
                    }
                    writer.WriteEndArray();
                    writer.WriteEndObject();
                }).ConfigureAwait(false);
                return;
            }

            if (context.Request.Method != HttpMethod.Post && context.Request.Method != HttpMethod.Delete)
            {
                SetMethodNotAllowed(context, "GET, HEAD, POST, DELETE");
                return;
            }

            await ExecuteCommandAsync(controlPlane, resourceContext, context).ConfigureAwait(false);
            return;
        }

        if (isNamespacedControlPlanePath)
        {
            context.Response.StatusCode = HttpStatusCode.NotFound;
            return;
        }

        await next.Invoke(context).ConfigureAwait(false);
    }

    /// <summary>Validates the identity required by a gateway-managed resource during composition.</summary>
    /// <param name="resourceContext">The resource identity and public application trust key.</param>
    /// <exception cref="ArgumentNullException"><paramref name="resourceContext"/> is null.</exception>
    /// <exception cref="InvalidOperationException">A managed resource lacks a resource name, application identity, or valid EC P-256 public trust key.</exception>
    /// <remarks>Standalone resources with no gateway identity require no bootstrap validation.</remarks>
    public static void Validate(ResourceContext resourceContext)
    {
        ArgumentNullException.ThrowIfNull(resourceContext);
        if (resourceContext.GatewayName is not null)
        {
            if (string.IsNullOrWhiteSpace(resourceContext.ResourceName))
            {
                throw new InvalidOperationException("A gateway-managed resource requires an ambient resource name.");
            }
            _ = GetVerifier(resourceContext);
        }
    }

    private static ResourceHealthReport MarkHostAsStarting(ResourceHealthReport report)
    {
        var contributions = new Dictionary<string, HealthContribution>(
            report.Contributions,
            StringComparer.Ordinal)
        {
            [HostReadinessContributionName] = HealthContribution.Unhealthy(
                "The Web host has not completed startup."),
        };

        return new ResourceHealthReport(
            HealthStatus.Unhealthy,
            new ReadOnlyDictionary<string, HealthContribution>(contributions));
    }

    private static async Task WriteHealthAsync(IHttpContext context, ResourceHealthReport report)
    {
        context.Response.StatusCode = report.Status is HealthStatus.Unhealthy
            ? HttpStatusCode.ServiceUnavailable
            : HttpStatusCode.Ok;
        context.Response.Headers[HttpHeaderKey.CacheControl] = "no-store, no-cache";

        await WriteJsonAsync(context, writer =>
        {
            writer.WriteStartObject();
            writer.WriteString("status", report.Status.ToString());
            writer.WritePropertyName("contributions");
            writer.WriteStartObject();
            foreach ((string name, HealthContribution contribution) in
                report.Contributions.OrderBy(static pair => pair.Key, StringComparer.Ordinal))
            {
                writer.WritePropertyName(name);
                writer.WriteStartObject();
                writer.WriteString("status", contribution.Status.ToString());
                if (contribution.Description is not null)
                {
                    writer.WriteString("description", contribution.Description);
                }
                if (contribution.Data is { Count: > 0 } data)
                {
                    writer.WritePropertyName("data");
                    writer.WriteStartObject();
                    foreach (KeyValuePair<string, object> item in data)
                    {
                        writer.WritePropertyName(item.Key);
                        WriteDataValue(writer, item.Value);
                    }
                    writer.WriteEndObject();
                }
                writer.WriteEndObject();
            }
            writer.WriteEndObject();
            writer.WriteEndObject();
        }, HealthJsonContentType).ConfigureAwait(false);
    }

    private static void WriteDataValue(Utf8JsonWriter writer, object? value)
    {
        switch (value)
        {
            case null:
                writer.WriteNullValue();
                break;
            case string text:
                writer.WriteStringValue(text);
                break;
            case bool flag:
                writer.WriteBooleanValue(flag);
                break;
            case int number:
                writer.WriteNumberValue(number);
                break;
            case long number:
                writer.WriteNumberValue(number);
                break;
            case double number:
                writer.WriteNumberValue(number);
                break;
            case float number:
                writer.WriteNumberValue(number);
                break;
            case decimal number:
                writer.WriteNumberValue(number);
                break;
            case Exception:
                // Defense in depth: the default control plane already replaces an exception in a
                // contribution's data, but a custom IResourceControlPlane reaches this writer unfiltered,
                // and an exception's ToString() is its type, message, inner exceptions and stack trace,
                // published on probes that are never authorized. The text is
                // ResourceControlPlane.ContributorExceptionDataValue (Hosting.Resources, internal).
                writer.WriteStringValue("The contributor reported an exception.");
                break;
            default:
                writer.WriteStringValue(value.ToString());
                break;
        }
    }

    private static async Task ExecuteCommandAsync(
        IResourceControlPlane controlPlane,
        ResourceContext resource,
        IHttpContext context)
    {
        string phase = "Envelope";
        ResourceCommand? command = null;
        ReadOnlyMemory<byte> response;
        try
        {
            using JsonDocument document = await JsonDocument.ParseAsync(
                context.Request.Body,
                cancellationToken: context.RequestCancelled).ConfigureAwait(false);
            JsonElement root = document.RootElement;
            if (root.ValueKind is not JsonValueKind.Object)
            {
                throw new JsonException("A resource command must be a JSON object.");
            }
            if (root.TryGetProperty("payload", out JsonElement rawPayload) && rawPayload.ValueKind is not JsonValueKind.String)
            {
                throw new JsonException("The command payload must be a base64 string.");
            }
            byte[] payload = root.TryGetProperty("payload", out JsonElement payloadElement)
                ? payloadElement.GetBytesFromBase64()
                : Array.Empty<byte>();
            command = new ResourceCommand(
                GetRequiredString(root, "id"),
                GetRequiredString(root, "kind"),
                GetRequiredString(root, "owner"),
                GetRequiredString(root, "key"),
                payload);

            phase = "Handler";
            response = context.Request.Method == HttpMethod.Delete
                ? await controlPlane.DeleteCommandAsync(command, context.RequestCancelled).ConfigureAwait(false)
                : await controlPlane.ExecuteCommandAsync(command, context.RequestCancelled).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is JsonException or FormatException or ArgumentException)
        {
            context.Response.StatusCode = HttpStatusCode.BadRequest;
            if (!Assimalign.Cohesion.Hosting.Resources.Internal.CommandFailureDiagnostics.TryConsume(exception, command))
            {
                WebHostingEventSource.Log.CommandMalformed(resource, context, phase, exception);
            }
            return;
        }
        catch (NotSupportedException exception)
        {
            context.Response.StatusCode = HttpStatusCode.NotImplemented;
            await WriteCommandRefusalAsync(context, exception.Message).ConfigureAwait(false);
            return;
        }
        catch (ResourceCommandRejectedException exception)
        {
            context.Response.StatusCode = HttpStatusCode.Conflict;
            await WriteCommandRefusalAsync(context, exception.Detail).ConfigureAwait(false);
            return;
        }

        context.Response.StatusCode = HttpStatusCode.Ok;
        context.Response.Headers[HttpHeaderKey.ContentType] = "application/octet-stream";
        await context.Response.Body.WriteAsync(response, context.RequestCancelled).ConfigureAwait(false);
    }

    private static Task WriteCommandRefusalAsync(IHttpContext context, string detail) =>
        WriteJsonAsync(context, writer =>
        {
            writer.WriteStartObject();
            writer.WriteString("status", "Rejected");
            writer.WriteString("detail", detail);
            writer.WriteEndObject();
        });

    private static string GetRequiredString(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out JsonElement property) ||
            property.ValueKind is not JsonValueKind.String || string.IsNullOrWhiteSpace(property.GetString()))
        {
            throw new JsonException($"The command property '{name}' is required.");
        }

        return property.GetString()!;
    }

    private static async Task WriteJsonAsync(
        IHttpContext context,
        Action<Utf8JsonWriter> write,
        string contentType = "application/json; charset=utf-8")
    {
        context.Response.Headers[HttpHeaderKey.ContentType] = contentType;

        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            write.Invoke(writer);
        }

        if (context.Request.Method != HttpMethod.Head)
        {
            await context.Response.Body.WriteAsync(buffer.WrittenMemory, context.RequestCancelled)
                .ConfigureAwait(false);
        }
    }

    private static void SetMethodNotAllowed(IHttpContext context, string allow)
    {
        context.Response.StatusCode = HttpStatusCode.MethodNotAllowed;
        context.Response.Headers[HttpHeaderKey.Allow] = allow;
    }

    private static bool IsNamespacedControlPlanePath(string path)
    {
        const string namespacePath = "/cohesion/v1";
        return path == namespacePath ||
            path.StartsWith(namespacePath + "/", StringComparison.Ordinal);
    }

    private static bool IsLoopbackPeer(IHttpContext context)
    {
        // Forwarded headers describe a claimed client, not the transport peer we trust here.
        if (context.ConnectionInfo.RemoteEndPoint is not IPEndPoint remote)
        {
            return false;
        }

        IPAddress address = remote.Address.IsIPv4MappedToIPv6
            ? remote.Address.MapToIPv4()
            : remote.Address;
        return address.Equals(IPAddress.Loopback) || address.Equals(IPAddress.IPv6Loopback);
    }

    private static async ValueTask<ControlPlaneCredentialVerification> AuthorizeAsync(IHttpContext context, ResourceContext resource)
    {
        ControlPlaneCredentialVerification result = await VerifyAsync(context, resource).ConfigureAwait(false);
        ResourceCredentialVerification verification = result.Verification;
        if (verification.Status is not ResourceCredentialStatus.Authorized)
        {
            return new(new ResourceCredentialVerification(
                verification.Status is ResourceCredentialStatus.Forbidden
                    ? ResourceCredentialStatus.Forbidden : ResourceCredentialStatus.Unauthorized, null, null), result.Reason);
        }

        // The management plane serves only this application's gateway. The default verifier already
        // pins iss to the application and sub to the gateway, so this is the mapped form of that rule
        // and only narrows what a registered verifier may admit.
        return verification.Caller is { Kind: ResourceCallerKind.Gateway } caller &&
            string.Equals(caller.Application, resource.ApplicationName, StringComparison.Ordinal) &&
            string.Equals(caller.Subject, resource.GatewayName, StringComparison.Ordinal)
                ? result
                : new(new ResourceCredentialVerification(ResourceCredentialStatus.Forbidden, null, null),
                    CredentialRejectionReason.CallerMismatch);
    }

    private static async ValueTask<ControlPlaneCredentialVerification> VerifyAsync(IHttpContext context, ResourceContext resource)
    {
        ResourceCredentialPresentation presentation = ResourceCredentialPresentation.FromAuthorizationValue(
            context.Request.Headers.TryGetValue(HttpHeaderKey.Authorization, out HttpHeaderValue header)
                ? header.Value
                : null,
            resource.ResourceName!,
            DateTimeOffset.UtcNow);
        if (ResourceRuntime.TryGetCredentialVerifier(resource, out IResourceCredentialVerifier? registered))
        {
            ResourceCredentialVerification result = await registered
                .VerifyAsync(presentation, context.RequestCancelled)
                .ConfigureAwait(false);
            if (result.Status is not ResourceCredentialStatus.NoResult)
            {
                return new(result, result.Status is ResourceCredentialStatus.Authorized
                    ? CredentialRejectionReason.None : CredentialRejectionReason.VerifierRejected);
            }
        }

        if (presentation.Scheme.Length == 0 || presentation.Credential.Length == 0)
        {
            return new(new ResourceCredentialVerification(ResourceCredentialStatus.Unauthorized, null, null),
                CredentialRejectionReason.MissingCredential);
        }

        return presentation.Scheme.Equals("Bearer", StringComparison.OrdinalIgnoreCase)
            ? GetVerifier(resource).Validate(presentation.Credential, presentation.ExpectedAudience, presentation.Now)
            : new(new ResourceCredentialVerification(ResourceCredentialStatus.Unauthorized, null, null),
                CredentialRejectionReason.UnsupportedScheme);
    }
}
