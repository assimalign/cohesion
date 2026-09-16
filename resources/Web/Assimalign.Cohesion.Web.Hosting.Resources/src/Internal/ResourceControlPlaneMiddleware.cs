using System;
using System.Buffers;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Core;
using Assimalign.Cohesion.Hosting;
using Assimalign.Cohesion.Hosting.Health;
using Assimalign.Cohesion.Hosting.Resources;
using Assimalign.Cohesion.Http;

namespace Assimalign.Cohesion.Web.Hosting.Resources;

/// <summary>Composes the resource management and probe protocol into a Web pipeline.</summary>
// Deviates from the repo interface-first rule per O35: hosts need one static composition
// entry shared with UseResourceControlPlane, without a runtime-module dependency.
public static class ResourceControlPlaneMiddleware
{
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
    /// including bare probes. Stop is deferred until response completion when the server supplies
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
        if (isNamespacedControlPlanePath && resourceContext.GatewayName is not null)
        {
            BootstrapTokenStatus status = Authorize(context, resourceContext);
            if (status is not BootstrapTokenStatus.Authorized)
            {
                context.Response.StatusCode = status is BootstrapTokenStatus.Forbidden
                    ? HttpStatusCode.Forbidden
                    : HttpStatusCode.Unauthorized;
                if (status is BootstrapTokenStatus.Unauthorized)
                {
                    context.Response.Headers[HttpHeaderKey.WWWAuthenticate] = "Bearer";
                }
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

            await ExecuteCommandAsync(controlPlane, context).ConfigureAwait(false);
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
            using var verifier = new BootstrapTokenVerifier(resourceContext);
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
            default:
                writer.WriteStringValue(value.ToString());
                break;
        }
    }

    private static async Task ExecuteCommandAsync(
        IResourceControlPlane controlPlane,
        IHttpContext context)
    {
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
            var command = new ResourceCommand(
                GetRequiredString(root, "id"),
                GetRequiredString(root, "kind"),
                GetRequiredString(root, "owner"),
                GetRequiredString(root, "key"),
                payload);

            ReadOnlyMemory<byte> response = context.Request.Method == HttpMethod.Delete
                ? await controlPlane.DeleteCommandAsync(command, context.RequestCancelled).ConfigureAwait(false)
                : await controlPlane.ExecuteCommandAsync(command, context.RequestCancelled).ConfigureAwait(false);
            context.Response.StatusCode = HttpStatusCode.Ok;
            context.Response.Headers[HttpHeaderKey.ContentType] = "application/octet-stream";
            await context.Response.Body.WriteAsync(response, context.RequestCancelled).ConfigureAwait(false);
        }
        catch (JsonException)
        {
            context.Response.StatusCode = HttpStatusCode.BadRequest;
        }
        catch (FormatException)
        {
            context.Response.StatusCode = HttpStatusCode.BadRequest;
        }
        catch (ArgumentException)
        {
            context.Response.StatusCode = HttpStatusCode.BadRequest;
        }
        catch (NotSupportedException exception)
        {
            context.Response.StatusCode = HttpStatusCode.NotImplemented;
            await WriteCommandRefusalAsync(context, exception.Message).ConfigureAwait(false);
        }
        catch (ResourceCommandRejectedException exception)
        {
            context.Response.StatusCode = HttpStatusCode.Conflict;
            await WriteCommandRefusalAsync(context, exception.Detail).ConfigureAwait(false);
        }
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

    private static BootstrapTokenStatus Authorize(IHttpContext context, ResourceContext resource)
    {
        if (!context.Request.Headers.TryGetValue(HttpHeaderKey.Authorization, out HttpHeaderValue header))
        {
            return BootstrapTokenStatus.Unauthorized;
        }

        const string prefix = "Bearer ";
        if (!header.Value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return BootstrapTokenStatus.Unauthorized;
        }

        using var verifier = new BootstrapTokenVerifier(resource);
        return verifier.Validate(header.Value[prefix.Length..], resource.ResourceName!, DateTimeOffset.UtcNow);
    }
}
