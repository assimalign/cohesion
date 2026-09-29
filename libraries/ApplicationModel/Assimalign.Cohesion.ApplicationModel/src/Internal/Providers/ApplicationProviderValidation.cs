using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;

namespace Assimalign.Cohesion.ApplicationModel.Internal;

/// <summary>
/// Validates an application's <see cref="ApplicationProviders"/> against its built model — at
/// <see cref="IApplicationBuilder.Build"/>, and for every application-set member when the set
/// starts, after it attaches the member's own registrations: every
/// <c>&lt;source&gt;:&lt;key&gt;</c> mount source of the application's own manifests has a
/// registered provider, every provider bound to a model resource names an existing resource of
/// the application with the kind the provider requires, no source or binding reaches into
/// another application, and every declared command whose target manifest marks it
/// <see cref="ResourceManifestCommand.RequiresInputResolver"/> has a registered
/// <see cref="IResourceCommandInputResolver"/> for its kind.
/// </summary>
/// <remarks>
/// <para>
/// "Own" manifests are those whose <see cref="ResourceManifest.Application"/> equals the model's
/// <see cref="IApplicationModel.Name"/> — the same test the gateway applies when it resolves
/// telemetry. Manifests of other applications (unrealized externals and Local <c>--realize</c>
/// closures) are not checked for mount sources. Resource names are unique within a model, so a
/// source or binding name identifies at most one resource.
/// </para>
/// <para>
/// Cross-application store sources are rejected (owner decision 4): a source is cross-application
/// when the consuming manifest references that resource in another application, or when the
/// model's resource of that name belongs to another application (a remote reference or external).
/// </para>
/// <para>
/// Manifest kinds compare with <see cref="StringComparison.OrdinalIgnoreCase"/>, matching the
/// gateway's existing kind checks. Every failure is an <see cref="InvalidOperationException"/>.
/// </para>
/// </remarks>
internal static class ApplicationProviderValidation
{
    /// <summary>The built-in <c>parameter:&lt;name&gt;</c> source name.</summary>
    internal const string ParameterSource = "parameter";

    /// <summary>The built-in <c>literal:&lt;value&gt;</c> source name.</summary>
    internal const string LiteralSource = "literal";

    private const string parameterPrefix = ParameterSource + ":";
    private const string literalPrefix = LiteralSource + ":";

    /// <summary>
    /// Validates <see cref="IApplicationModel.Providers"/> against <paramref name="model"/>.
    /// </summary>
    /// <param name="model">The built model.</param>
    /// <param name="applicationSetMember">
    /// <see langword="true"/> when <paramref name="model"/> is an application-set member whose
    /// registrations come from its <c>AddApplication(..., configure)</c> callback, so a missing
    /// registration names that callback rather than <c>builder.Use&lt;Area&gt;(...)</c>. The rules
    /// are identical either way.
    /// </param>
    /// <exception cref="ArgumentNullException"><paramref name="model"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">
    /// A mount source has no provider, names a resource of another application, or is malformed;
    /// or a provider binding names a missing resource, a resource of another application, or a
    /// resource whose kind differs from the provider's <c>ResourceKind</c>; or the telemetry sink
    /// resource does not declare its endpoint; or a declared command requires an input resolver
    /// that is not registered.
    /// </exception>
    internal static void Validate(IApplicationModel model, bool applicationSetMember = false)
    {
        ArgumentNullException.ThrowIfNull(model);
        ApplicationProviders providers = model.Providers ?? ApplicationProviders.Empty;

        ValidateMountSources(model, providers, applicationSetMember);
        ValidateSourceBindings(model, providers);
        ValidateCommandInputs(model, providers, applicationSetMember);

        if (providers.CertificateAuthority is { } authority)
        {
            ValidateBinding(
                model,
                "certificate authority",
                authority.Resource,
                authority.Provider.ResourceKind,
                authority.Provider.GetType().Name);
        }

        if (providers.TrustStore is { } trustStore)
        {
            ValidateBinding(
                model,
                "trust store",
                trustStore.Resource,
                trustStore.Provider.ResourceKind,
                trustStore.Provider.GetType().Name);
        }

        if (providers.Telemetry is { Resource: ResourceName sink } telemetry)
        {
            ResourceManifest manifest = ResolveBoundResource(
                model,
                "telemetry sink",
                sink.ToString(),
                requiredKind: null,
                providerName: nameof(ResourceTelemetrySink));
            if (!DeclaresEndpoint(manifest, telemetry.EndpointName))
            {
                throw new InvalidOperationException(
                    $"The telemetry sink '{sink}' declares no '{telemetry.EndpointName}' endpoint. " +
                    "Pass the name of the sink's OTLP endpoint to ResourceTelemetrySink.FromResource(...).");
            }
        }
    }

    private static void ValidateMountSources(
        IApplicationModel model,
        ApplicationProviders providers,
        bool applicationSetMember)
    {
        IReadOnlyList<ResourceManifest> manifests = model.Manifests;
        for (int index = 0; index < manifests.Count; index++)
        {
            ResourceManifest manifest = manifests[index];
            if (manifest.Application != model.Name)
            {
                continue;
            }

            foreach (ResourceManifestMount mount in manifest.Mounts)
            {
                string? source = mount.Source;
                if (string.IsNullOrEmpty(source) ||
                    source.StartsWith(parameterPrefix, StringComparison.Ordinal) ||
                    source.StartsWith(literalPrefix, StringComparison.Ordinal))
                {
                    continue;
                }

                if (!TryParseSourceName(source, out string sourceName))
                {
                    throw new InvalidOperationException(
                        $"Resource '{manifest.Name}' mount '{mount.Name}' source '{source}' must use " +
                        "parameter:<name>, literal:<value>, or <source>:<key>.");
                }

                if (TryFindOtherApplication(model, manifest, sourceName, out ApplicationName owner))
                {
                    throw new InvalidOperationException(
                        $"Resource '{manifest.Name}' mount '{mount.Name}' reads '{source}' from '{sourceName}', " +
                        $"a resource of application '{owner}'. Cross-application store sources are not supported " +
                        $"yet: declare the store in application '{model.Name}', or supply the value through a " +
                        "parameter: source.");
                }

                if (!providers.Sources.ContainsKey(sourceName))
                {
                    throw new InvalidOperationException(
                        $"Resource '{manifest.Name}' mount '{mount.Name}' reads '{source}', but no provider is " +
                        $"registered for source '{sourceName}'. " +
                        DescribeProviderHint(model, sourceName, applicationSetMember));
                }
            }
        }
    }

    // Mirrors delivery: the gateway resolves a command's inputs with the resolvers of the application
    // that declares it (ApplicationGateway.ResolveCommandInputAsync), whether the target is its own
    // resource or another application's. The target's manifest says whether the resolved form is
    // required, so the check names no area and reads only the manifest.
    private static void ValidateCommandInputs(
        IApplicationModel model,
        ApplicationProviders providers,
        bool applicationSetMember)
    {
        IReadOnlyList<IResourceCommand> commands = model.Commands;
        if (commands.Count == 0)
        {
            return;
        }

        IReadOnlyList<IApplicationResourceDescriptor> descriptors = model.Descriptors;
        IReadOnlyList<ResourceManifest> manifests = model.Manifests;
        foreach (IResourceCommand command in commands)
        {
            ResourceManifest? target = null;
            for (int index = 0; index < descriptors.Count && index < manifests.Count; index++)
            {
                if (ReferenceEquals(descriptors[index].Resource, command.Target))
                {
                    target = manifests[index];
                    break;
                }
            }

            if (target is null || !RequiresInputResolver(target, command.Kind) ||
                HasCommandInputResolver(providers, command.Kind))
            {
                continue;
            }

            throw new InvalidOperationException(
                $"Application '{model.Name}' declares command '{command.Kind}' (key '{command.Key}') for resource " +
                $"'{target.Name}', which requires the declaring application to resolve that command's inputs " +
                $"before delivery, but no {nameof(IResourceCommandInputResolver)} is registered for " +
                $"'{command.Kind}'. " + DescribeCommandInputHint(model, target, command.Kind, applicationSetMember));
        }
    }

    private static bool RequiresInputResolver(ResourceManifest manifest, string commandKind)
    {
        foreach (ResourceManifestCommand declared in manifest.Commands)
        {
            if (declared.RequiresInputResolver &&
                string.Equals(declared.Kind, commandKind, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static bool HasCommandInputResolver(ApplicationProviders providers, string commandKind)
    {
        foreach (IResourceCommandInputResolver resolver in providers.CommandInputs)
        {
            if (string.Equals(resolver.CommandKind, commandKind, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static string DescribeCommandInputHint(
        IApplicationModel model,
        ResourceManifest target,
        string commandKind,
        bool applicationSetMember)
    {
        string kind = target.Kind;
        string package = string.IsNullOrWhiteSpace(kind)
            ? "the target area's Assimalign.Cohesion.<Area>.ApplicationModel.Orchestration package"
            : $"Assimalign.Cohesion.{kind}.ApplicationModel.Orchestration";
        string surface = applicationSetMember ? "application" : "builder";

        if (target.Application != model.Name)
        {
            // Use<Area>(...) would bind a store of another application, which provider validation
            // rejects, so the resolver is registered on its own.
            return $"'{target.Name}' is a resource of application '{target.Application}': add an " +
                $"{nameof(IResourceCommandInputResolver)} for '{commandKind}' to {surface}.Providers.CommandInputs " +
                $"(the resolver {package} ships).";
        }

        string verb = string.IsNullOrWhiteSpace(kind) ? "Use<Area>" : $"Use{kind}";
        if (applicationSetMember)
        {
            return $"Reference {package} and register it where the set adds the application, " +
                $"set.AddApplication(Applications.<Member>, application => application.{verb}(\"{target.Name}\")), " +
                $"or add an {nameof(IResourceCommandInputResolver)} for '{commandKind}' to " +
                "application.Providers.CommandInputs inside that callback.";
        }

        return $"Reference {package} and call builder.{verb}(...) for '{target.Name}', or add an " +
            $"{nameof(IResourceCommandInputResolver)} for '{commandKind}' to builder.Providers.CommandInputs.";
    }

    private static void ValidateSourceBindings(IApplicationModel model, ApplicationProviders providers)
    {
        var names = new List<string>(providers.Sources.Keys);
        names.Sort(StringComparer.Ordinal);
        foreach (string name in names)
        {
            IResourceSourceProvider provider = providers.Sources[name];
            string role = $"mount source '{name}'";
            if (TryFindManifest(model, name, own: false, out ResourceManifest? foreign) &&
                !TryFindManifest(model, name, own: true, out _))
            {
                throw CrossApplicationBinding(role, provider.GetType().Name, name, foreign.Application);
            }

            if (provider.ResourceKind is not null)
            {
                ResolveBoundResource(model, role, name, provider.ResourceKind, provider.GetType().Name);
            }
        }
    }

    private static void ValidateBinding(
        IApplicationModel model,
        string role,
        ResourceName? resource,
        string? requiredKind,
        string providerName)
    {
        if (resource is ResourceName name)
        {
            ResolveBoundResource(model, role, name.ToString(), requiredKind, providerName);
            return;
        }

        if (requiredKind is not null)
        {
            throw new InvalidOperationException(
                $"The {role} provider '{providerName}' requires a '{requiredKind}' resource, but its binding " +
                $"names no resource. Bind it to the {requiredKind} resource of application '{model.Name}'.");
        }
    }

    private static ResourceManifest ResolveBoundResource(
        IApplicationModel model,
        string role,
        string name,
        string? requiredKind,
        string providerName)
    {
        if (!TryFindManifest(model, name, own: true, out ResourceManifest? manifest))
        {
            if (TryFindManifest(model, name, own: false, out ResourceManifest? foreign))
            {
                throw CrossApplicationBinding(role, providerName, name, foreign.Application);
            }

            throw new InvalidOperationException(
                $"The {role} provider '{providerName}' is bound to resource '{name}', which is not a resource " +
                $"of application '{model.Name}'. Add the resource to the application, or bind the provider " +
                "to an existing one.");
        }

        if (requiredKind is not null &&
            !string.Equals(manifest.Kind, requiredKind, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"The {role} provider '{providerName}' requires a resource of kind '{requiredKind}', but " +
                $"resource '{name}' is kind '{manifest.Kind}'.");
        }

        return manifest;
    }

    private static InvalidOperationException CrossApplicationBinding(
        string role,
        string providerName,
        string name,
        ApplicationName owner) =>
        new($"The {role} provider '{providerName}' is bound to resource '{name}' of application '{owner}'. " +
            "Cross-application providers are not supported yet: bind a resource of the declaring application.");

    private static string DescribeProviderHint(IApplicationModel model, string sourceName, bool applicationSetMember)
    {
        if (applicationSetMember)
        {
            // A member's describe output carries no registrations: they are made where the set adds
            // the member, for that member alone.
            const string registration = "set.AddApplication(Applications.<Member>, application => application.";
            if (TryFindManifest(model, sourceName, own: true, out ResourceManifest? member) &&
                !string.IsNullOrWhiteSpace(member.Kind))
            {
                string kind = member.Kind;
                return $"'{sourceName}' is a {kind} resource of application '{model.Name}': reference " +
                    $"Assimalign.Cohesion.{kind}.ApplicationModel.Orchestration and register it where the set adds " +
                    $"the application, {registration}Use{kind}(\"{sourceName}\")), or register an " +
                    $"{nameof(IResourceSourceProvider)} in application.Providers.Sources[\"{sourceName}\"] inside that " +
                    "callback. A member never inherits another application's registrations.";
            }

            return $"Register an {nameof(IResourceSourceProvider)} for application '{model.Name}' where the set adds " +
                $"it, {registration}Providers.Sources[\"{sourceName}\"] = provider); a store resource's " +
                "Assimalign.Cohesion.<Area>.ApplicationModel.Orchestration package registers one through its " +
                "Use<Area>(\"<store>\") verb. A member never inherits another application's registrations.";
        }

        if (TryFindManifest(model, sourceName, own: true, out ResourceManifest? manifest) &&
            !string.IsNullOrWhiteSpace(manifest.Kind))
        {
            string kind = manifest.Kind;
            return $"'{sourceName}' is a {kind} resource: reference " +
                $"Assimalign.Cohesion.{kind}.ApplicationModel.Orchestration and call builder.Use{kind}(...), " +
                $"or register an {nameof(IResourceSourceProvider)} in builder.Providers.Sources[\"{sourceName}\"].";
        }

        return $"Register an {nameof(IResourceSourceProvider)} in builder.Providers.Sources[\"{sourceName}\"]; " +
            "a store resource's Assimalign.Cohesion.<Area>.ApplicationModel.Orchestration package registers " +
            "one through its builder.Use<Area>(...) verb.";
    }

    private static bool TryFindOtherApplication(
        IApplicationModel model,
        ResourceManifest consumer,
        string sourceName,
        out ApplicationName owner)
    {
        foreach (ResourceManifestReference reference in consumer.References)
        {
            if (string.Equals(reference.Resource.ToString(), sourceName, StringComparison.Ordinal) &&
                reference.Application != consumer.Application)
            {
                owner = reference.Application;
                return true;
            }
        }

        if (!TryFindManifest(model, sourceName, own: true, out _) &&
            TryFindManifest(model, sourceName, own: false, out ResourceManifest? foreign))
        {
            owner = foreign.Application;
            return true;
        }

        owner = default;
        return false;
    }

    private static bool TryFindManifest(
        IApplicationModel model,
        string name,
        bool own,
        [NotNullWhen(true)] out ResourceManifest? manifest)
    {
        IReadOnlyList<ResourceManifest> manifests = model.Manifests;
        for (int index = 0; index < manifests.Count; index++)
        {
            ResourceManifest candidate = manifests[index];
            if ((candidate.Application == model.Name) == own &&
                string.Equals(candidate.Name.ToString(), name, StringComparison.Ordinal))
            {
                manifest = candidate;
                return true;
            }
        }

        manifest = null;
        return false;
    }

    private static bool DeclaresEndpoint(ResourceManifest manifest, string? endpoint)
    {
        foreach (ResourceManifestEndpoint declared in manifest.Endpoints)
        {
            if (string.Equals(declared.Name, endpoint, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static bool TryParseSourceName(string source, out string sourceName)
    {
        int separator = source.IndexOf(':');
        if (separator <= 0 ||
            separator == source.Length - 1 ||
            source.IndexOf(':', separator + 1) >= 0 ||
            string.IsNullOrWhiteSpace(source[..separator]) ||
            string.IsNullOrWhiteSpace(source[(separator + 1)..]))
        {
            sourceName = string.Empty;
            return false;
        }

        sourceName = source[..separator];
        return true;
    }
}
