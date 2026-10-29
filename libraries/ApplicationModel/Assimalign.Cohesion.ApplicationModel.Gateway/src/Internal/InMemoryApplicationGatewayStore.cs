using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.ApplicationModel.Gateway.Internal;

// The store of a gateway created without one: the record lives as long as the gateway object, so a
// new gateway starts with none and deploys everything its provider plans.
internal sealed class InMemoryApplicationGatewayStore : IApplicationGatewayStore
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
