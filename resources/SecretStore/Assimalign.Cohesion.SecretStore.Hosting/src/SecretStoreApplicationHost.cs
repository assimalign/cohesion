using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Hosting;
using Assimalign.Cohesion.SecretStore;

namespace Assimalign.Cohesion.SecretStore.Hosting;

internal sealed class SecretStoreApplicationHost : Host<SecretStoreApplicationContext>, ISecretStoreApplication
{
    private readonly SecretStoreApplicationContext _context;

    internal SecretStoreApplicationHost(
        SecretStoreApplicationOptions options,
        SecretStoreApplicationContext context)
        : base(options)
    {
        _context = context;
    }

    public override SecretStoreApplicationContext Context => _context;

    async Task ISecretStoreApplication.RunAsync(CancellationToken cancellationToken)
    {
        // TODO(design item 12): Route RunAsync through ResourceRuntime once the ambient runtime seam exists.
        if (cancellationToken.IsCancellationRequested)
        {
            await ((IHost)this).StartAsync(CancellationToken.None).ConfigureAwait(false);
            await ((IHost)this).StopAsync(CancellationToken.None).ConfigureAwait(false);
            return;
        }

        await base.RunAsync(cancellationToken).ConfigureAwait(false);
    }
}
