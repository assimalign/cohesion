using System;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Hosting;
using Assimalign.Cohesion.SecretStore;

namespace Assimalign.Cohesion.SecretStore.Hosting;

internal sealed class SecretStoreApplicationHost : Host<SecretStoreApplicationContext>, ISecretStoreApplication
{
    private readonly SecretStoreApplicationContext _context;
    private readonly SecretsEndpointService _endpointService;
    private bool _endpointServiceDisposed;

    internal SecretStoreApplicationHost(
        SecretStoreApplicationOptions options,
        SecretStoreApplicationContext context,
        SecretsEndpointService endpointService)
        : base(options)
    {
        _context = context;
        _endpointService = endpointService;
    }

    public override SecretStoreApplicationContext Context => _context;

    async Task ISecretStoreApplication.RunAsync(CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            await ((IHost)this).StartAsync(CancellationToken.None).ConfigureAwait(false);
            await ((IHost)this).StopAsync(CancellationToken.None).ConfigureAwait(false);
            return;
        }

        await base.RunAsync(cancellationToken).ConfigureAwait(false);
    }

    protected override async ValueTask DisposeAsync(bool disposing)
    {
        try
        {
            await base.DisposeAsync(disposing).ConfigureAwait(false);
        }
        finally
        {
            if (disposing && !_endpointServiceDisposed)
            {
                _endpointServiceDisposed = true;
                _endpointService.Dispose();
            }
        }
    }
}
