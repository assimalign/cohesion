using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.ApplicationModel;

/// <summary>
/// A built application: the model of what its gateway deploys, and the gateway that runs it.
/// </summary>
/// <remarks>
/// <see cref="CreateBuilder(GatewayName, string[])"/> starts composing one; the builder's
/// <see cref="ApplicationBuilder.Build"/> creates it.
/// </remarks>
[DebuggerDisplay("{Model.Name}")]
[DebuggerTypeProxy(typeof(DebugView))]
public sealed class Application : IApplication
{
    private readonly IApplicationGateway? _gateway;
    private int _running;

    internal Application(ApplicationModel model, IApplicationGateway? gateway)
    {
        Model = model;
        _gateway = gateway;
    }

    /// <inheritdoc/>
    public IApplicationModel Model { get; }

    /// <summary>
    /// Creates a builder for the application of the specified root gateway.
    /// </summary>
    /// <param name="name">The root gateway's name.</param>
    /// <param name="args">The command-line arguments the application was started with.</param>
    /// <returns>The builder: the application's root set.</returns>
    /// <exception cref="ArgumentException"><paramref name="name"/> is <see langword="default"/>.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="args"/> is <see langword="null"/>.</exception>
    public static ApplicationBuilder CreateBuilder(GatewayName name, string[] args)
    {
        if (name.Value is null)
        {
            throw new ArgumentException(ErrorMessages.GatewayNameMissing, nameof(name));
        }

        ArgumentNullException.ThrowIfNull(args);

        return new ApplicationBuilder(name, args);
    }

    /// <inheritdoc/>
    public Task RunAsync(CancellationToken cancellationToken = default)
    {
        if (_gateway is null)
        {
            throw new InvalidOperationException(ErrorMessages.ApplicationGatewayMissing);
        }

        if (Interlocked.Exchange(ref _running, 1) != 0)
        {
            throw new InvalidOperationException(ErrorMessages.ApplicationAlreadyRun);
        }

        return _gateway.RunAsync(Model, cancellationToken);
    }

    private sealed class DebugView
    {
        private readonly Application _application;

        public DebugView(Application application)
        {
            _application = application;
        }

        public IApplicationModel Model => _application.Model;
    }
}
