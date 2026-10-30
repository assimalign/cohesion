using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.ApplicationModel.Gateway.InProcess.Tests;

/// <summary>
/// A record store that outlives the gateways that use it, as a file or a database would.
/// </summary>
internal sealed class TestStore : IApplicationGatewayStore
{
    private readonly Lock _gate = new();
    private readonly Dictionary<GatewayName, ApplicationGatewayRecord> _records = [];

    public ValueTask<ApplicationGatewayRecord?> LoadAsync(GatewayName gateway, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            return ValueTask.FromResult(_records.TryGetValue(gateway, out ApplicationGatewayRecord? record) ? record : null);
        }
    }

    public ValueTask<ApplicationGatewayRecord> AcquireAsync(GatewayName gateway, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            ApplicationGatewayRecord current = _records.TryGetValue(gateway, out ApplicationGatewayRecord? record) ? record : new ApplicationGatewayRecord(gateway, []);
            ApplicationGatewayRecord acquired = current with { Epoch = current.Epoch + 1 };
            _records[gateway] = acquired;
            return ValueTask.FromResult(acquired);
        }
    }

    public ValueTask<bool> SaveAsync(ApplicationGatewayRecord record, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            if (_records.TryGetValue(record.Gateway, out ApplicationGatewayRecord? stored) && stored.Epoch > record.Epoch)
            {
                return ValueTask.FromResult(false);
            }

            _records[record.Gateway] = record;
            return ValueTask.FromResult(true);
        }
    }
}
