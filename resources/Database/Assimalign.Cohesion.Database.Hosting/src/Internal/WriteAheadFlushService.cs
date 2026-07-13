using System;
using System.Threading;

namespace Assimalign.Cohesion.Database.Hosting.Internal;

using Assimalign.Cohesion.Hosting;

/// <summary>
/// The write-ahead flush worker slot on the execution menu.
/// </summary>
/// <remarks>
/// Dedicated OS thread per the execution menu: a synchronous blocking flush loop must
/// own its thread for its whole life rather than occupy the pool. Today this is a
/// documented placeholder that parks until the host stops: durability is engine-owned
/// (requirement R10) — the SQL engine flushes synchronously at commit
/// (steal/no-force WAL), so there is no host-driven flush work to do, and an embedded
/// consumer must get the same durability without a host. This is the slot for the
/// engine-owned WAL group-commit flusher planned in #902 (under the engine
/// self-sufficiency feature #862) — see the "Execution-model mapping" section of
/// docs/DESIGN.md for the full worker inventory. It never owns durability itself, or
/// embedded consumers would silently lose it.
/// </remarks>
internal sealed class WriteAheadFlushService : DedicatedThreadService
{
    protected override void Run(CancellationToken cancellationToken)
    {
        // Placeholder: park until shutdown. See the class remarks for why there is no
        // host-driven flush work today and where the real worker will live.
        cancellationToken.WaitHandle.WaitOne();
    }
}
