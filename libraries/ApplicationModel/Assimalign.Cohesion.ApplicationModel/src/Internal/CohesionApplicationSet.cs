using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.ApplicationModel;

internal sealed class CohesionApplicationSet : IApplicationSet
{
    private readonly IMultiModelApplicationGateway _gateway;
    private readonly List<ApplicationDeclaration> _applications = new();
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

    public IApplicationSet AddApplication(ApplicationDeclaration application)
    {
        ArgumentNullException.ThrowIfNull(application);
        for (int index = 0; index < _applications.Count; index++)
        {
            if (_applications[index].Name == application.Name)
            {
                throw new InvalidOperationException(
                    $"Application '{application.Name}' is already present in this application set.");
            }
        }

        _applications.Add(application);
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
            models[index] = await _applications[index].Resolver.ResolveAsync(
                context,
                cancellationToken).ConfigureAwait(false);
            if (models[index].Name != _applications[index].Name)
            {
                throw new InvalidOperationException(
                    $"Application declaration '{_applications[index].Name}' resolved model " +
                    $"'{models[index].Name}'. The control plane returned the wrong application.");
            }
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
                _gateway.Validate(models);
                if (_gateway is not IApplicationGatewayRenderer renderer)
                {
                    throw new NotSupportedException(
                        $"Gateway '{_gateway.Name}' does not implement render mode.");
                }

                await renderer.RenderAsync(models, Console.Out, cancellationToken)
                    .ConfigureAwait(false);
                break;
            default:
                throw new NotSupportedException(
                    $"Application-set run mode '{RunMode}' is not supported by this control-plane composition seam.");
        }
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

        if (!Environment.IsDevelopment)
        {
            throw new InvalidOperationException(
                "--realize is Development-only and cannot be used in this application environment.");
        }

        string gateway = _gateway.Name.ToString();
        if (!string.Equals(gateway, "local", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(gateway, "inprocess", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(gateway, "docker", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Gateway '{gateway}' cannot honor --realize. " +
                "Use the Local, InProcess, or Docker gateway in Development.");
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

        await _gateway.StartAsync(models, cancellation.Token).ConfigureAwait(false);
        await stopped.Task.ConfigureAwait(false);
        await _gateway.StopAsync(CancellationToken.None).ConfigureAwait(false);
    }
}
