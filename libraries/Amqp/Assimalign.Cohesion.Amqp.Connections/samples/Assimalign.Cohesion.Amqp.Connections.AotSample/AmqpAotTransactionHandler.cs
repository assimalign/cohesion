using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Amqp.Connections.AotSample;

/// <summary>
/// The guard's transactional resource: it numbers transactions from one and records each discharge.
/// </summary>
internal sealed class AmqpAotTransactionHandler : IAmqpTransactionHandler
{
    private readonly Lock _sync = new();
    private readonly List<(byte[] TransactionId, bool Fail)> _discharges = [];
    private byte _next;

    /// <summary>
    /// Gets the discharges the coordinator performed, in order.
    /// </summary>
    public IReadOnlyList<(byte[] TransactionId, bool Fail)> Discharges
    {
        get
        {
            lock (_sync)
            {
                return [.. _discharges];
            }
        }
    }

    public ValueTask<ReadOnlyMemory<byte>> DeclareAsync(AmqpDeclare declare, CancellationToken cancellationToken = default)
    {
        lock (_sync)
        {
            return ValueTask.FromResult<ReadOnlyMemory<byte>>(new[] { ++_next });
        }
    }

    public ValueTask DischargeAsync(ReadOnlyMemory<byte> transactionId, bool fail, CancellationToken cancellationToken = default)
    {
        lock (_sync)
        {
            _discharges.Add((transactionId.ToArray(), fail));
        }

        return ValueTask.CompletedTask;
    }
}
