using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Hosting;
using Assimalign.Cohesion.ConfigurationStore;

namespace Assimalign.Cohesion.ConfigurationStore.Hosting;

internal sealed class ConfigurationStoreApplicationHost : Host<ConfigurationStoreApplicationContext>, IConfigurationStoreApplication
{
    private readonly ConfigurationStoreApplicationContext _context;

    internal ConfigurationStoreApplicationHost(
        ConfigurationStoreApplicationOptions options,
        ConfigurationStoreApplicationContext context)
        : base(options)
    {
        _context = context;
    }

    public override ConfigurationStoreApplicationContext Context => _context;

    async Task IConfigurationStoreApplication.RunAsync(CancellationToken cancellationToken)
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
