using System;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.ApplicationModel;
using Assimalign.Cohesion.ApplicationModel.Gateway;

namespace GatewaySmoke;

/// <summary>
/// Verifies that a contributed provider receives the original gateway argument array.
/// </summary>
public static class JitTestGatewayCommandLine
{
    private static int applyCount;

    /// <summary>Applies the test provider's required command-line token.</summary>
    /// <param name="options">The selected provider's options.</param>
    /// <param name="args">The original gateway argument array.</param>
    /// <exception cref="ArgumentException">The provider token is absent.</exception>
    public static void Apply(LocalGatewayOptions options, string[] args)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(args);

        if (Interlocked.Increment(ref applyCount) is not 1)
        {
            throw new InvalidOperationException(
                "The JitTest provider command-line hook ran more than once.");
        }

        if (!Array.Exists(
                args,
                static argument => string.Equals(
                    argument,
                    "--jit-provider-token=received",
                    StringComparison.Ordinal)))
        {
            throw new ArgumentException(
                "The JitTest provider did not receive its command-line token.",
                nameof(args));
        }
    }
}

/// <summary>A no-op gateway used to execute the contributed provider path.</summary>
public sealed class JitTestGateway : IApplicationGateway
{
    /// <summary>Initializes the gateway with the generated provider options.</summary>
    /// <param name="options">The options completed by generated provider dispatch.</param>
    public JitTestGateway(LocalGatewayOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
    }

    /// <inheritdoc/>
    public ResourceName Name => "jit-test";

    /// <inheritdoc/>
    public void Validate(IApplicationModel model) => ArgumentNullException.ThrowIfNull(model);

    /// <inheritdoc/>
    public Task StartAsync(
        IApplicationModel model,
        CancellationToken cancellationToken = default) => Task.CompletedTask;

    /// <inheritdoc/>
    public Task ReconcileAsync(
        IApplicationModel model,
        CancellationToken cancellationToken = default) => Task.CompletedTask;

    /// <inheritdoc/>
    public Task StopAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    /// <inheritdoc/>
    public Task UninstallAsync(
        IApplicationModel model,
        CancellationToken cancellationToken = default) => Task.CompletedTask;
}
