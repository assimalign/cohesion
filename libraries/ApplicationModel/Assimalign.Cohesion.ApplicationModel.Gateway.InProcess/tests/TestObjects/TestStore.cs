using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.ApplicationModel.Gateway.InProcess.Tests;

/// <summary>
/// A record store that outlives the gateways that use it, as a file or a database would.
/// </summary>
internal sealed class TestStore : IApplicationGatewayStore
{
    private readonly ConcurrentDictionary<GatewayName, ApplicationGatewayRecord> _records = new();

    public ValueTask<ApplicationGatewayRecord?> LoadAsync(GatewayName gateway, CancellationToken cancellationToken = default)
    {
        return ValueTask.FromResult(_records.TryGetValue(gateway, out ApplicationGatewayRecord? record) ? record : null);
    }

    public ValueTask SaveAsync(ApplicationGatewayRecord record, CancellationToken cancellationToken = default)
    {
        _records[record.Gateway] = record;
        return ValueTask.CompletedTask;
    }
}
