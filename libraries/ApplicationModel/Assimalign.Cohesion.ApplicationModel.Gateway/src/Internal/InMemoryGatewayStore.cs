using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.ApplicationModel.Gateway.Internal;

// The store of a gateway created without one: the record lives as long as the gateway object, so a
// new gateway starts with none and deploys everything its provider plans.
internal sealed class InMemoryGatewayStore : IGatewayStore
{
    private readonly ConcurrentDictionary<GatewayName, GatewayRecord> _records = new();

    public ValueTask<GatewayRecord?> LoadAsync(GatewayName gateway, CancellationToken cancellationToken = default)
    {
        return ValueTask.FromResult(_records.TryGetValue(gateway, out GatewayRecord? record) ? record : null);
    }

    public ValueTask SaveAsync(GatewayRecord record, CancellationToken cancellationToken = default)
    {
        _records[record.Gateway] = record;
        return ValueTask.CompletedTask;
    }
}
