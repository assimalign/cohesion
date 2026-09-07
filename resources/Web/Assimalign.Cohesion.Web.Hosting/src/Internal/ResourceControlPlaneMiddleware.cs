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
using Assimalign.Cohesion.Http;

namespace Assimalign.Cohesion.Web.Hosting.Internal;

internal static class ResourceControlPlaneMiddleware
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

    internal static Task InvokeAsync(
        IResourceControlPlane controlPlane,
        int? controlPlanePort,
        IHttpContext context,
        WebApplicationMiddleware next)
    {
        return InvokeAsync(
            controlPlane,
            ReadOnlyMemory<byte>.Empty,
            requireAuthentication: false,
            controlPlanePort,
            isApplicationReady: true,
            context,
            next);
    }

    internal static async Task InvokeAsync(
        IResourceControlPlane controlPlane,
        ReadOnlyMemory<byte> bootstrapCredential,
        int? controlPlanePort,
        IHttpContext context,
        WebApplicationMiddleware next)
    {
        await InvokeAsync(
                controlPlane,
                bootstrapCredential,
                requireAuthentication: false,
                controlPlanePort,
                isApplicationReady: true,
                context,
                next)
            .ConfigureAwait(false);
    }

    internal static async Task InvokeAsync(
        IResourceControlPlane controlPlane,
        ReadOnlyMemory<byte> bootstrapCredential,
        bool requireAuthentication,
        int? controlPlanePort,
        bool isApplicationReady,
        IHttpContext context,
        WebApplicationMiddleware next)
    {
        if (controlPlanePort is not int port || context.ConnectionInfo.LocalPort != port)
        {
            await next.Invoke(context).ConfigureAwait(false);
            return;
        }

        string path = context.Request.Path.Value;
        bool isRead = context.Request.Method == HttpMethod.Get || context.Request.Method == HttpMethod.Head;

        bool isNamespacedControlPlanePath = IsNamespacedControlPlanePath(path);
        if (isNamespacedControlPlanePath &&
            !IsAuthorized(context, bootstrapCredential.Span, requireAuthentication))
        {
            context.Response.StatusCode = HttpStatusCode.Unauthorized;
            context.Response.Headers[HttpHeaderKey.WWWAuthenticate] = "Bearer";
            return;
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

            ResponseCompletionFeature? responseCompletion =
                context.Features.Get<ResponseCompletionFeature>();

            if (responseCompletion is not null)
            {
                responseCompletion.Register(
                    () => controlPlane.RequestStopAsync(CancellationToken.None));
            }
            else
            {
                // A custom IWebApplicationServer may execute this pipeline without the default
                // server's completion feature. Preserve the control-plane terminal's historical
                // behavior in that case; the built-in server takes the deterministic deferred path.
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
                    writer.WriteEndObject();
                }).ConfigureAwait(false);
                return;
            }

            if (context.Request.Method != HttpMethod.Post)
            {
                SetMethodNotAllowed(context, "GET, HEAD, POST");
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
            byte[] payload = root.TryGetProperty("payload", out JsonElement payloadElement)
                ? payloadElement.GetBytesFromBase64()
                : Array.Empty<byte>();
            var command = new ResourceCommand(
                GetRequiredString(root, "id"),
                GetRequiredString(root, "kind"),
                GetRequiredString(root, "owner"),
                GetRequiredString(root, "key"),
                payload);

            ReadOnlyMemory<byte> response = await controlPlane.ExecuteCommandAsync(
                command,
                context.RequestCancelled).ConfigureAwait(false);
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
        catch (NotSupportedException)
        {
            context.Response.StatusCode = HttpStatusCode.NotImplemented;
        }
    }

    private static string GetRequiredString(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out JsonElement property) ||
            property.ValueKind is not JsonValueKind.String)
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

    private static bool IsAuthorized(
        IHttpContext context,
        ReadOnlySpan<byte> bootstrapCredential,
        bool requireAuthentication)
    {
        if (bootstrapCredential.IsEmpty)
        {
            return !requireAuthentication;
        }

        if (!context.Request.Headers.TryGetValue(
                HttpHeaderKey.Authorization,
                out HttpHeaderValue authorization))
        {
            return false;
        }

        const string bearerPrefix = "Bearer ";
        string value = authorization.Value;
        if (!value.StartsWith(bearerPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        byte[] presentedCredential = Encoding.UTF8.GetBytes(value[bearerPrefix.Length..]);
        return CryptographicOperations.FixedTimeEquals(
            presentedCredential,
            bootstrapCredential);
    }
}
