using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.ApplicationModel.Internal;

internal sealed class CohesionApplicationSet : IApplicationSet
{
    private readonly IMultiModelApplicationGateway _gateway;
    private readonly List<Member> _applications = new();
    private readonly IReadOnlyList<string> _externalBindings;
    private readonly IReadOnlyList<ResourceName> _realize;

    public CohesionApplicationSet(
        IMultiModelApplicationGateway gateway,
        IApplicationEnvironment environment,
        GatewayRunMode runMode,
        IReadOnlyList<string> externalBindings,
        IReadOnlyList<ResourceName> realize)
    {
        _gateway = gateway ?? throw new ArgumentNullException(nameof(gateway));
        Environment = environment ?? throw new ArgumentNullException(nameof(environment));
        RunMode = runMode;
        _externalBindings = externalBindings ?? throw new ArgumentNullException(nameof(externalBindings));
        _realize = realize ?? throw new ArgumentNullException(nameof(realize));
    }

    public IApplicationEnvironment Environment { get; }

    public GatewayRunMode RunMode { get; }

    public IApplicationSet AddApplication(ApplicationDeclaration application) =>
        Add(application, configure: null);

    public IApplicationSet AddApplication(
        ApplicationDeclaration application,
        Action<IApplicationProviderBuilder> configure)
    {
        ArgumentNullException.ThrowIfNull(application);
        ArgumentNullException.ThrowIfNull(configure);
        return Add(application, configure);
    }

    private CohesionApplicationSet Add(
        ApplicationDeclaration application,
        Action<IApplicationProviderBuilder>? configure)
    {
        ArgumentNullException.ThrowIfNull(application);
        for (int index = 0; index < _applications.Count; index++)
        {
            if (_applications[index].Declaration.Name == application.Name)
            {
                throw new InvalidOperationException(
                    $"Application '{application.Name}' is already present in this application set.");
            }
        }

        _applications.Add(new Member(application, configure));
        return this;
    }

    public async Task RunAsync(CancellationToken cancellationToken = default)
    {
        if (_applications.Count == 0)
        {
            throw new InvalidOperationException("An application set must declare at least one application.");
        }

        ValidateRealizationRequest();
        var context = new ApplicationModelResolutionContext(
            Environment,
            RunMode,
            _gateway.Name,
            _realize);
        var models = new IApplicationModel[_applications.Count];
        for (int index = 0; index < models.Length; index++)
        {
            ApplicationDeclaration declaration = _applications[index].Declaration;
            IApplicationModel resolved = await declaration.Resolver.ResolveAsync(
                context,
                cancellationToken).ConfigureAwait(false);
            if (resolved.Name != declaration.Name)
            {
                throw new InvalidOperationException(
                    $"Application declaration '{declaration.Name}' resolved model " +
                    $"'{resolved.Name}'. The control plane returned the wrong application.");
            }

            models[index] = BindProviders(resolved, _applications[index].Configure);
        }

        BindExternalResources(models);

        ValidateRequestedRealizations(models);

        switch (RunMode)
        {
            case GatewayRunMode.Run:
                _gateway.Validate(models);
                await RunLifetimeAsync(models, cancellationToken).ConfigureAwait(false);
                break;
            case GatewayRunMode.Apply:
                _gateway.Validate(models);
                await _gateway.ReconcileAsync(models, cancellationToken).ConfigureAwait(false);
                break;
            case GatewayRunMode.Teardown:
                _gateway.Validate(models);
                await _gateway.UninstallAsync(models, cancellationToken).ConfigureAwait(false);
                break;
            case GatewayRunMode.Describe:
                await ApplicationModelDocumentWriter.WriteAsync(models, cancellationToken)
                    .ConfigureAwait(false);
                break;
            case GatewayRunMode.Render:
                if (_gateway is not IApplicationGatewayRenderer renderer)
                {
                    throw new NotSupportedException(
                        $"Gateway '{_gateway.Name}' does not implement render mode.");
                }

                _gateway.Validate(models);
                await renderer.RenderAsync(models, Console.Out, cancellationToken)
                    .ConfigureAwait(false);
                break;
            case GatewayRunMode.Bootstrap:
                if (_gateway is not IApplicationGatewayBootstrapper bootstrapper)
                {
                    throw new NotSupportedException(
                        $"Gateway '{_gateway.Name}' does not implement bootstrap mode.");
                }

                _gateway.Validate(models);
                await bootstrapper.BootstrapAsync(models, Console.Out, cancellationToken)
                    .ConfigureAwait(false);
                break;
            default:
                throw new NotSupportedException(
                    $"Application-set run mode '{RunMode}' is not supported by this control-plane composition seam.");
        }
    }

    // Providers are code, so a member model imported from a describe output or an export carries
    // none. A member gets exactly the registrations its own AddApplication callback made - never
    // another member's, and never anything by source or resource name - and is validated with the
    // rules Build() applies to a builder, so a store-backed member fails here, naming the member
    // and the registration to add, before the gateway realizes anything.
    private static IApplicationModel BindProviders(
        IApplicationModel model,
        Action<IApplicationProviderBuilder>? configure)
    {
        IApplicationModel bound = model;
        if (configure is not null)
        {
            if ((model.Providers ?? ApplicationProviders.Empty).HasRegistrations)
            {
                throw new InvalidOperationException(
                    $"Application-set member '{model.Name}' resolved a model that already carries provider " +
                    "registrations, and the set registers providers for it as well. Register the member's " +
                    "providers in one place: either where its model is built, or in its AddApplication " +
                    "callback.");
            }

            var registration = new ApplicationSetMemberProviderBuilder(model);
            try
            {
                configure(registration);
            }
            catch (Exception exception) when (
                exception is ArgumentException or InvalidOperationException or NotSupportedException)
            {
                throw new InvalidOperationException(
                    $"Application-set member '{model.Name}' could not register its providers: {exception.Message}",
                    exception);
            }

            bound = new ProviderBoundApplicationModel(model, registration.Complete());
        }

        try
        {
            ApplicationProviderValidation.Validate(bound, applicationSetMember: true);
        }
        catch (InvalidOperationException exception)
        {
            throw new InvalidOperationException(
                $"Application-set member '{model.Name}' has invalid provider registrations. {exception.Message}",
                exception);
        }

        return bound;
    }

    private void ValidateRequestedRealizations(IReadOnlyList<IApplicationModel> models)
    {
        for (int requestIndex = 0; requestIndex < _realize.Count; requestIndex++)
        {
            bool found = false;
            for (int modelIndex = 0; modelIndex < models.Count && !found; modelIndex++)
            {
                for (int resourceIndex = 0; resourceIndex < models[modelIndex].Resources.Count; resourceIndex++)
                {
                    if (models[modelIndex].Resources[resourceIndex] is ExternalResource external
                        && external.IsRealized
                        && external.Name == _realize[requestIndex])
                    {
                        found = true;
                        break;
                    }
                }
            }

            if (!found)
            {
                throw new InvalidOperationException(
                    $"--realize names external '{_realize[requestIndex]}', but no application-set " +
                    "member described that resource as realized.");
            }
        }
    }

    private void ValidateRealizationRequest()
    {
        if (_realize.Count == 0)
        {
            return;
        }

        if (!Environment.IsLocal)
        {
            throw new InvalidOperationException(
                "--realize is Local-only and cannot be used in this application environment.");
        }

        string gateway = _gateway.Name.ToString();
        if (!string.Equals(gateway, "local", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(gateway, "inprocess", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(gateway, "docker", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Gateway '{gateway}' cannot honor --realize. " +
                "Use the Local, InProcess, or Docker gateway in Local.");
        }
    }

    private void BindExternalResources(IReadOnlyList<IApplicationModel> models)
    {
        for (int modelIndex = 0; modelIndex < models.Count; modelIndex++)
        {
            IApplicationModel model = models[modelIndex];
            for (int resourceIndex = 0; resourceIndex < model.Resources.Count; resourceIndex++)
            {
                if (model.Resources[resourceIndex] is not ExternalResource external)
                {
                    continue;
                }

                IExternalResourceResolver? configured = ExternalBindingOverrides.FromEnvironment(
                    external.Declaration);
                IExternalResourceResolver? commandLine = ExternalBindingOverrides.FromCommandLine(
                    external.Declaration,
                    _externalBindings);
                IExternalResourceResolver fallback = commandLine ?? configured ?? external.Resolver;

                if (_gateway is IApplicationSetExternalResourceResolver direct
                    && ContainsApplication(models, external.Declaration.Application))
                {
                    external.Bind(new InSetExternalResourceResolver(direct, models, fallback));
                }
                else if (commandLine is not null || configured is not null)
                {
                    external.Bind(fallback);
                }
            }
        }
    }

    private static bool ContainsApplication(
        IReadOnlyList<IApplicationModel> models,
        ApplicationName application)
    {
        for (int index = 0; index < models.Count; index++)
        {
            if (models[index].Name == application)
            {
                return true;
            }
        }

        return false;
    }

    private async Task RunLifetimeAsync(
        IReadOnlyList<IApplicationModel> models,
        CancellationToken cancellationToken)
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var stopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using CancellationTokenRegistration registration = cancellation.Token.Register(
            static state => ((TaskCompletionSource)state!).TrySetResult(),
            stopped);

        try
        {
            await _gateway.StartAsync(models, cancellation.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            await _gateway.StopAsync(CancellationToken.None).ConfigureAwait(false);
            return;
        }

        await stopped.Task.ConfigureAwait(false);
        await _gateway.StopAsync(CancellationToken.None).ConfigureAwait(false);
    }

    /// <summary>
    /// One declared member: its declaration and, when the set registers providers for it, the
    /// registration callback.
    /// </summary>
    /// <param name="Declaration">The generated member application declaration.</param>
    /// <param name="Configure">The member's provider registration callback, or <see langword="null"/>.</param>
    private readonly record struct Member(
        ApplicationDeclaration Declaration,
        Action<IApplicationProviderBuilder>? Configure);
}
