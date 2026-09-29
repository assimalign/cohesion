using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.ApplicationModel.Gateway;

public abstract partial class ApplicationGateway
{
    private static ApplicationProviders GetProviders(IApplicationModel model) =>
        model.Providers ?? ApplicationProviders.Empty;

    // Resolves one declared mount source. parameter: and literal: are built into the gateway;
    // every other '<source>:<key>' expression goes to the provider the model registered for
    // <source> in ApplicationProviders.Sources. The gateway knows no store kind, route, or
    // document format: a failure becomes an Unresolved input that the controller refuses before
    // realization, never an exception that aborts the pass.
    private async ValueTask<ResourceMountInput> ResolveMountSourceAsync(
        IResourceControlContext context,
        ResourcePlan plan,
        MountBinding mount,
        CancellationToken cancellationToken)
    {
        string source = mount.Source!;
        if (source.StartsWith(LiteralPrefix, StringComparison.Ordinal))
        {
            return mount.Kind == ResourceMountKind.Configuration
                ? ResourceMountInput.Resolved(source, Encoding.UTF8.GetBytes(source[LiteralPrefix.Length..]))
                : ResourceMountInput.Unresolved(
                    source,
                    $"Literal source '{source}' is allowed only for Configuration mounts; " +
                    $"mount '{mount.Mount}' on resource '{plan.Resource}' is '{mount.Kind}'.");
        }

        if (source.StartsWith(ParameterPrefix, StringComparison.Ordinal))
        {
            string parameter = source[ParameterPrefix.Length..];
            if (mount.Kind == ResourceMountKind.Volume)
            {
                return ResourceMountInput.Unresolved(
                    source,
                    $"Volume mount '{mount.Mount}' on resource '{plan.Resource}' cannot declare a source.");
            }

            if (string.IsNullOrWhiteSpace(parameter))
            {
                return ResourceMountInput.Unresolved(
                    source,
                    $"Parameter source for mount '{mount.Mount}' on resource '{plan.Resource}' has no name.");
            }

            return GetParameters(context.Model.Name).TryGetValue(parameter, out string? value)
                ? ResourceMountInput.Resolved(source, Encoding.UTF8.GetBytes(value))
                : ResourceMountInput.Unresolved(
                    source,
                    $"Parameter '{parameter}' required by mount '{mount.Mount}' on resource " +
                    $"'{plan.Resource}' is not bound for gateway '{Name}'.");
        }

        if (!TryParseSource(source, out string? sourceName, out string? key))
        {
            return ResourceMountInput.Unresolved(
                source,
                $"Mount source '{source}' on resource '{plan.Resource}' must use " +
                "parameter:<name>, literal:<value>, or <source>:<key>.");
        }

        if (mount.Kind == ResourceMountKind.Volume)
        {
            return ResourceMountInput.Unresolved(
                source,
                $"Volume mount '{mount.Mount}' on resource '{plan.Resource}' cannot declare a source.");
        }

        return await ResolveProviderSourceAsync(context, plan, mount, source, sourceName!, key!, cancellationToken)
            .ConfigureAwait(false);
    }

    private async ValueTask<ResourceMountInput> ResolveProviderSourceAsync(
        IResourceControlContext context,
        ResourcePlan plan,
        MountBinding mount,
        string source,
        string sourceName,
        string key,
        CancellationToken cancellationToken)
    {
        IApplicationModel model = context.Model;
        ResourceManifest consumer = GetConsumerManifest(context);
        if (consumer.Application != model.Name)
        {
            // A resource of another application realized in this model (a Local --realize
            // closure): the model's registrations serve its own resources only and are never
            // inherited by source name, and cross-application store sources are not supported yet.
            return ResourceMountInput.Unresolved(
                source,
                DescribeForeignConsumerSource(model, consumer, plan, mount, source, sourceName));
        }

        ApplicationProviders providers = GetProviders(model);
        if (!providers.Sources.TryGetValue(sourceName, out IResourceSourceProvider? provider))
        {
            return ResourceMountInput.Unresolved(
                source,
                DescribeMissingSourceProvider(model, providers, plan, mount, source, sourceName));
        }

        // A provider that declares a resource kind, or a source that names a resource of this
        // model, is resource-backed: the gateway checks the dependency, waits for Running, and
        // hands the provider an authenticated connection. Anything else lives outside the model
        // and authenticates itself.
        ResourceProviderConnection? connection = null;
        if (provider.ResourceKind is not null || NamesModelResource(context, sourceName))
        {
            if (!TryResolveSourceConnection(
                    context,
                    provider,
                    sourceName,
                    out ResourceManifest? store,
                    out Uri? controlPlaneAddress,
                    out string? failure))
            {
                return ResourceMountInput.Unresolved(source, failure!);
            }

            connection = await CreateProviderConnectionAsync(model, store!, controlPlaneAddress!, cancellationToken)
                .ConfigureAwait(false);
        }

        var request = new ResourceSourceRequest(model.Name, plan.Resource, mount.Mount, mount.Kind, key, connection);
        if (mount.Kind == ResourceMountKind.Secret && IsCertificateMount(context, mount.Mount))
        {
            return await ReadSourceCertificateAsync(context, plan, mount, provider, request, sourceName, cancellationToken)
                .ConfigureAwait(false);
        }

        try
        {
            if (mount.Kind == ResourceMountKind.Secret)
            {
                ReadOnlyMemory<byte> secret = await provider
                    .ReadSecretAsync(request, cancellationToken)
                    .ConfigureAwait(false);
                return ResourceMountInput.Resolved(source, secret);
            }

            IReadOnlyDictionary<string, string?> configuration = await provider
                .ReadConfigurationAsync(request, cancellationToken)
                .ConfigureAwait(false)
                ?? throw new InvalidDataException(
                    $"Source provider '{provider.GetType().Name}' returned no configuration.");
            // The mount byte format is the gateway's contract, not the provider's: a JSON object
            // with ordinally sorted keys and null values kept.
            return ResourceMountInput.Resolved(source, SerializeConfiguration(configuration));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (IsProviderFailure(exception))
        {
            return ResourceMountInput.Unresolved(
                source,
                $"Mount '{mount.Mount}' on resource '{plan.Resource}' could not resolve " +
                $"'{source}' through '{sourceName}': {exception.Message}");
        }
    }

    private async ValueTask<ResourceMountInput> ReadSourceCertificateAsync(
        IResourceControlContext context,
        ResourcePlan plan,
        MountBinding mount,
        IResourceSourceProvider provider,
        ResourceSourceRequest request,
        string sourceName,
        CancellationToken cancellationToken)
    {
        string source = mount.Source!;
        ResourceCertificate certificate;
        try
        {
            certificate = await provider
                .ReadCertificateAsync(request, cancellationToken)
                .ConfigureAwait(false)
                ?? throw new InvalidDataException(
                    $"Source provider '{provider.GetType().Name}' returned no certificate.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (IsProviderFailure(exception))
        {
            return ResourceMountInput.Unresolved(
                source,
                $"Certificate '{request.Key}' for mount '{mount.Mount}' on resource " +
                $"'{plan.Resource}' is not available from source '{sourceName}': {exception.Message}");
        }

        ResourceMountInput input = ValidateCertificateInput(
            plan,
            mount,
            ResourceMountInput.Resolved(source, Encoding.UTF8.GetBytes(certificate.CertificatePem)));
        if (input.IsResolved && !input.Content.IsEmpty)
        {
            GetCertificateAuthority(context.Model.Name).AddAnchors(certificate.TrustAnchorsPem);
        }

        return input;
    }

    // Resolves the model resource a resource-backed source names and its control-plane address,
    // or why the provider cannot be connected to it; the caller mints the connection's credential.
    private static bool TryResolveSourceConnection(
        IResourceControlContext context,
        IResourceSourceProvider provider,
        string sourceName,
        out ResourceManifest? store,
        out Uri? controlPlaneAddress,
        out string? failure)
    {
        store = null;
        controlPlaneAddress = null;
        if (!TryResolveSourceResource(
                context,
                (ResourceName)sourceName,
                out ResourceManifest? manifest,
                out ResourceDependencyObservation? observation,
                out failure))
        {
            return false;
        }

        // Owner decision 4: a store of another application is not a source yet. Build() rejects
        // it for the application's own manifests; this guards models Build() never validated.
        // The consumer is the model's own resource (ResolveProviderSourceAsync refuses others).
        ResourceManifest consumer = GetConsumerManifest(context);
        if (manifest!.Application != consumer.Application)
        {
            failure = $"Mount source resource '{manifest.Application}/{manifest.Name}' belongs to " +
                $"application '{manifest.Application}', but resource '{consumer.Name}' belongs to " +
                $"'{consumer.Application}'. Cross-application store sources are not supported yet.";
            return false;
        }

        if (provider.ResourceKind is string kind &&
            !string.Equals(manifest.Kind, kind, StringComparison.OrdinalIgnoreCase))
        {
            failure = $"Source '{sourceName}' is registered with provider '{provider.GetType().Name}', " +
                $"which reads '{kind}' resources, but resource '{manifest.Name}' is kind '{manifest.Kind}'.";
            return false;
        }

        if (!TryResolveControlPlaneEndpoint(manifest, observation!, out Uri? endpoint, out failure))
        {
            return false;
        }

        if (!CanSendCredential(context.Model, endpoint!, out failure))
        {
            return false;
        }

        store = manifest;
        controlPlaneAddress = endpoint;
        return true;
    }

    // The connection a provider uses to call one model resource's control plane: the caller is
    // the application, the address is the observed endpoint plus the manifest's control-plane
    // path, and the bearer is the pass's ResourceAccess credential for that resource (its
    // audience), from the registered credential issuer or the default ES256 issuer.
    private async ValueTask<ResourceProviderConnection> CreateProviderConnectionAsync(
        IApplicationModel model,
        ResourceManifest resource,
        Uri controlPlaneAddress,
        CancellationToken cancellationToken)
    {
        ApplicationCredential credential = await GetOrIssueResourceCredentialAsync(
                model,
                resource.Name,
                ApplicationCredentialPurpose.ResourceAccess,
                cancellationToken)
            .ConfigureAwait(false);
        return new ResourceProviderConnection(
            model.Name,
            resource.Name,
            resource.Kind,
            controlPlaneAddress,
            RequireBearer(credential, ApplicationCredentialPurpose.ResourceAccess),
            // The resource presents a certificate issued under ITS application's authority, so the
            // transport trust is keyed on the resource's owner, not on the calling application.
            string.Equals(controlPlaneAddress.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
                ? CreateOutboundTrustValidator(resource.Application)
                : null);
    }

    private static bool NamesModelResource(IResourceControlContext context, string sourceName)
    {
        IReadOnlyList<ResourceManifest> manifests = context.Model.Manifests;
        for (int index = 0; index < manifests.Count; index++)
        {
            if (string.Equals(manifests[index].Name.ToString(), sourceName, StringComparison.Ordinal))
            {
                return true;
            }
        }

        IReadOnlyList<ResourceDependencyObservation> observed = context.ObservedDependencies;
        for (int index = 0; index < observed.Count; index++)
        {
            if (string.Equals(observed[index].Resource.ToString(), sourceName, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static ResourceManifest GetConsumerManifest(IResourceControlContext context) =>
        context.Model.Manifests[IndexOfDescriptor(context.Model.Descriptors, context.Descriptor)];

    // Models imported from an application-model document (application-set members and exports)
    // carry ApplicationProviders.Empty and never inherit another model's registrations; an
    // application set attaches a member's own registrations from its AddApplication callback and
    // validates them before the gateway sees the model. A model that reaches the gateway without the
    // registration a mount needs - for example one handed to the gateway directly - fails here,
    // with the missing registration named, rather than silently resolving nothing.
    private static string DescribeMissingSourceProvider(
        IApplicationModel model,
        ApplicationProviders providers,
        ResourcePlan plan,
        MountBinding mount,
        string source,
        string sourceName)
    {
        var message = new StringBuilder()
            .Append("Mount '").Append(mount.Mount).Append("' on resource '").Append(plan.Resource)
            .Append("' reads '").Append(source).Append("', but application '").Append(model.Name)
            .Append("' registers no provider for source '").Append(sourceName).Append("'.");
        if (ReferenceEquals(providers, ApplicationProviders.Empty))
        {
            message.Append(" The model carries no provider registrations: a model imported from an ")
                .Append("application-model document (an application-set member or an export) never inherits ")
                .Append("another model's registrations. An application set registers a member's providers ")
                .Append("explicitly, for that member alone: set.AddApplication(Applications.<Member>, ")
                .Append("application => application.Use<Area>(\"").Append(sourceName).Append("\")).");
        }

        message.Append(" Register an ").Append(nameof(IResourceSourceProvider))
            .Append(" in builder.Providers.Sources[\"").Append(sourceName)
            .Append("\"] where the application is built; a store resource's ")
            .Append("Assimalign.Cohesion.<Area>.ApplicationModel.Orchestration package registers one through ")
            .Append("its builder.Use<Area>(...) verb.");
        return message.ToString();
    }

    // A Local --realize closure's resources belong to the realized application. The composing
    // application's registrations are for its own resources, so none of them - resource-backed or
    // not - is applied to the closure by source name (owner decision 4 rejects the store case).
    private static string DescribeForeignConsumerSource(
        IApplicationModel model,
        ResourceManifest consumer,
        ResourcePlan plan,
        MountBinding mount,
        string source,
        string sourceName) =>
        $"Mount '{mount.Mount}' on resource '{plan.Resource}' reads '{source}', but resource " +
        $"'{plan.Resource}' belongs to application '{consumer.Application}' and is realized in application " +
        $"'{model.Name}''s model. Application '{model.Name}''s provider registrations apply only to its own " +
        $"resources and are never inherited by source name, and cross-application store sources are not " +
        $"supported yet, so source '{sourceName}' cannot be bound here. Run application " +
        $"'{consumer.Application}' through its own gateway, where its registrations apply.";

    private static bool TryParseSource(string source, out string? sourceName, out string? key)
    {
        int separator = source.IndexOf(':');
        if (separator <= 0 || separator == source.Length - 1 ||
            source.IndexOf(':', separator + 1) >= 0)
        {
            sourceName = null;
            key = null;
            return false;
        }

        sourceName = source[..separator];
        key = source[(separator + 1)..];
        if (string.IsNullOrWhiteSpace(sourceName) || string.IsNullOrWhiteSpace(key))
        {
            sourceName = null;
            key = null;
            return false;
        }

        return true;
    }

    // The exceptions the provider contracts document for a source, authority, or store that
    // cannot serve a request. Anything else is a defect and propagates.
    private static bool IsProviderFailure(Exception exception) =>
        exception is HttpRequestException or InvalidDataException or JsonException or
            NotSupportedException or ArgumentException;
}
