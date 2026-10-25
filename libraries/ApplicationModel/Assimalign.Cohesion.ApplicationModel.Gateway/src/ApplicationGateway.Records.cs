using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.ApplicationModel.Gateway;

public abstract partial class ApplicationGateway
{
    private static readonly IReadOnlyDictionary<ApplicationName, ReadOnlyMemory<byte>> _noClaims =
        new ReadOnlyDictionary<ApplicationName, ReadOnlyMemory<byte>>(new Dictionary<ApplicationName, ReadOnlyMemory<byte>>());

    // Each realized resource's generation for the session: the plan and the artifact do not change within one.
    private readonly Dictionary<ApplicationResourceKey, string> _generations = new();

    /// <summary>
    /// Gets the store that keeps this gateway's deployment records, or <see langword="null"/> when the platform
    /// delivers none. The default is <see langword="null"/>; Local and in-process gateways keep records as files.
    /// </summary>
    /// <remarks>
    /// With a store, every realized resource receives its record directory as
    /// <see cref="AppEnvironment.Variables.ConfigDirectory"/> before it starts, so management changes reach it later
    /// without a restart. Without one, a resource cannot carry
    /// deployment configuration, and the management API refuses record operations.
    /// </remarks>
    protected virtual IResourceRecordStore? RecordStore => null;

    /// <summary>Gets the plan extension keys this gateway's compiler reads, such as <c>kubernetes/hpa</c>.</summary>
    /// <remarks>
    /// A plan extension whose platform part is this gateway's <see cref="Name"/> and that this collection lacks fails
    /// validation; keys of other platforms are ignored. The default supports none.
    /// </remarks>
    protected virtual IReadOnlyCollection<ResourceExtensionKey> SupportedExtensions => Array.Empty<ResourceExtensionKey>();

    /// <summary>
    /// Describes the build an artifact deploys, so a resource whose build changed starts a new deployment generation
    /// while re-applying the same build keeps the current one and its management overrides.
    /// </summary>
    /// <param name="artifact">The gathered artifact.</param>
    /// <returns>
    /// A container image's digest, or a hash of an executable and its managed entry assembly; empty for any other
    /// artifact. A gateway with its own artifact form overrides this.
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="artifact"/> is null.</exception>
    /// <exception cref="IOException">An executable cannot be read.</exception>
    protected virtual string DescribeArtifact(IResourceArtifact artifact)
    {
        ArgumentNullException.ThrowIfNull(artifact);
        switch (artifact)
        {
            case IContainerImageArtifact image:
                return image.Digest;
            case IExecutableArtifact executable:
                using (var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
                {
                    AppendFile(hash, executable.ExecutablePath);
                    // An apphost is unchanged by a rebuild; the managed assembly beside it carries the code.
                    string assembly = executable.ExecutablePath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                        ? executable.ExecutablePath[..^4] + ".dll"
                        : executable.ExecutablePath + ".dll";
                    if (File.Exists(assembly))
                    {
                        AppendFile(hash, assembly);
                    }

                    return "sha256:" + Convert.ToHexStringLower(hash.GetHashAndReset());
                }
            default:
                return string.Empty;
        }
    }

    // Writes the plan's deployment layer into the resource's record before the controller realizes the resource,
    // and returns inputs that deliver the record. A deployment whose generation is unchanged keeps the record's
    // override layer; a new generation drops it, and the drop is reported. Claims survive either way.
    private async ValueTask<ResourceInputs> DeliverRecordAsync(
        ModelResource item,
        ResourceInputs inputs,
        CancellationToken cancellationToken)
    {
        IResourceRecordStore? store = RecordStore;
        ResourcePlan plan = item.Descriptor.Plan!;
        if (store is null || IsExternalPlan(plan))
        {
            return inputs;
        }

        ApplicationName application = item.Model.Name;
        ResourceName resource = item.Descriptor.Resource.Name;
        string generation = GetGeneration(item, plan);
        byte[] deployment = WriteDeploymentLayer(plan.Configuration);
        for (int attempt = 0; ; attempt++)
        {
            ResourceRecord? current = await store.ReadAsync(application, resource, cancellationToken).ConfigureAwait(false);
            if (current is not null &&
                string.Equals(current.Generation, generation, StringComparison.Ordinal) &&
                current.Deployment.Span.SequenceEqual(deployment))
            {
                break;
            }

            bool sameGeneration = string.Equals(current?.Generation, generation, StringComparison.Ordinal);
            ResourceRecord next = ResourceRecord.Create(
                generation,
                deployment,
                current?.Claims ?? _noClaims,
                sameGeneration ? current!.Override : ReadOnlyMemory<byte>.Empty);
            // A management write that lands between the read and this write moves the version; read it again.
            if (await store.TryWriteAsync(application, resource, next, current?.Version, cancellationToken).ConfigureAwait(false))
            {
                if (!sameGeneration && current is not null && !current.Override.IsEmpty)
                {
                    await ReportDiscardedOverridesAsync(item, current, generation, cancellationToken).ConfigureAwait(false);
                }

                break;
            }

            if (attempt == 2)
            {
                throw new InvalidOperationException(string.Format(
                    CultureInfo.CurrentCulture, ErrorMessages.ResourceRecordContended, application, resource));
            }
        }

        return inputs.WithConfigurationDirectory(store.GetInstanceDirectory(application, resource));
    }

    // Overrides last until the resource's next deployment (decision D3), so a new generation discards them; the
    // operator learns which sections reverted to code, on standard error and in the application's telemetry.
    private async ValueTask ReportDiscardedOverridesAsync(
        ModelResource item,
        ResourceRecord discarded,
        string generation,
        CancellationToken cancellationToken)
    {
        var sections = new List<string>();
        using (JsonDocument layer = JsonDocument.Parse(discarded.Override))
        {
            foreach (JsonProperty section in layer.RootElement.EnumerateObject())
            {
                sections.Add(section.Name);
            }
        }

        string detail = string.Format(
            CultureInfo.CurrentCulture,
            ErrorMessages.ResourceOverridesDiscarded,
            item.Descriptor.Resource.Name,
            generation,
            string.Join(", ", sections));
        Console.Error.WriteLine(detail);
        await EmitManagementEventAsync(
                item.Model,
                item.Descriptor.Resource.Name,
                ResourceManagementOperations.DiscardOverrides,
                target: string.Join(",", sections),
                caller: null,
                generation,
                detail,
                cancellationToken)
            .ConfigureAwait(false);
    }

    // The hash of the resource's deployment content: its plan, which carries the deployment layer and the platform
    // extensions, and the build it deploys. Sixteen hex digits name a generation in platform objects.
    private string GetGeneration(ModelResource item, ResourcePlan plan)
    {
        if (_generations.TryGetValue(item.Key, out string? generation))
        {
            return generation;
        }

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(JsonSerializer.SerializeToUtf8Bytes(plan, ResourcePlanJsonContext.Default.ResourcePlan));
        hash.AppendData([0]);
        hash.AppendData(Encoding.UTF8.GetBytes(DescribeArtifact(_artifacts[item.Key])));
        generation = Convert.ToHexStringLower(hash.GetHashAndReset().AsSpan(0, 8));
        _generations[item.Key] = generation;
        return generation;
    }

    // The deployment layer's sections in ordinal order, so equal content writes equal bytes.
    private static byte[] WriteDeploymentLayer(IReadOnlyDictionary<string, JsonElement> configuration)
    {
        if (configuration.Count == 0)
        {
            return Array.Empty<byte>();
        }

        var sections = new List<string>(configuration.Keys);
        sections.Sort(StringComparer.Ordinal);
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
        {
            writer.WriteStartObject();
            foreach (string section in sections)
            {
                writer.WritePropertyName(section);
                configuration[section].WriteTo(writer);
            }

            writer.WriteEndObject();
        }

        return stream.ToArray();
    }

    // A plan carries deployment configuration only to a gateway that delivers records, and extensions for this
    // gateway's platform only when its compiler knows them.
    private void ValidateDeploymentData(IApplicationResourceDescriptor descriptor, ResourcePlan plan)
    {
        if (plan.Configuration.Count > 0 && RecordStore is null)
        {
            throw new InvalidOperationException(string.Format(
                CultureInfo.CurrentCulture, ErrorMessages.ResourceRecordStoreMissing, Name, descriptor.Resource.Name));
        }

        foreach (string key in plan.Extensions.Keys)
        {
            if (!ResourceExtensionKey.IsValid(key, out _))
            {
                continue;
            }

            ResourceExtensionKey extension = key;
            bool owned = string.Equals(extension.Platform, Name.ToString(), StringComparison.OrdinalIgnoreCase);
            if (owned && !Contains(SupportedExtensions, extension))
            {
                throw new InvalidOperationException(string.Format(
                    CultureInfo.CurrentCulture, ErrorMessages.ResourceExtensionUnsupported, Name, descriptor.Resource.Name, key));
            }
        }
    }

    private static bool Contains(IReadOnlyCollection<ResourceExtensionKey> keys, ResourceExtensionKey key)
    {
        foreach (ResourceExtensionKey candidate in keys)
        {
            if (candidate == key)
            {
                return true;
            }
        }

        return false;
    }

    private static void AppendFile(IncrementalHash hash, string path)
    {
        using FileStream stream = File.OpenRead(path);
        byte[] buffer = new byte[81920];
        int read;
        while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
        {
            hash.AppendData(buffer, 0, read);
        }
    }
}
