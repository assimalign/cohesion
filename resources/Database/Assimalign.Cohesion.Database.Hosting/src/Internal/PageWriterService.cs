using System;
using System.Threading;

namespace Assimalign.Cohesion.Database.Hosting.Internal;

using Assimalign.Cohesion.Hosting;

/// <summary>
/// The page-writer worker slot on the execution menu.
/// </summary>
/// <remarks>
/// Dedicated OS thread per the execution menu: a synchronous blocking write-back loop
/// must own its thread for its whole life rather than occupy the pool. Today this is a
/// documented placeholder that parks until the host stops: page write-back is
/// engine-owned (requirement R10) — the buffer pool's write-ahead gate keeps dirty
/// pages steal-safe and the engine writes them back within its own storage layer, and
/// an embedded consumer must get the same behavior without a host. When the engine
/// grows a host-mappable background page-writer/checkpoint worker seam (filed follow-up
/// under the engine self-sufficiency feature #862), this slot drives it.
/// </remarks>
internal sealed class PageWriterService : DedicatedThreadService
{
    protected override void Run(CancellationToken cancellationToken)
    {
        // Placeholder: park until shutdown. See the class remarks for why there is no
        // host-driven page-writer work today and where the real worker will live.
        cancellationToken.WaitHandle.WaitOne();
    }
}
