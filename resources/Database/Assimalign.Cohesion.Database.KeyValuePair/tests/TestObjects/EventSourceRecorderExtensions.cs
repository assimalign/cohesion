using System;
using System.Diagnostics.Tracing;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Testing;

namespace Assimalign.Cohesion.Database.KeyValuePair.Tests;

internal static class EventSourceRecorderExtensions
{
    extension(EventSourceRecorder recorder)
    {
        public async Task<EventWrittenEventArgs> WaitForAsync(Func<EventWrittenEventArgs, bool> match, CancellationToken cancellationToken)
        {
            while (true)
            {
                foreach (EventWrittenEventArgs written in recorder.Events)
                {
                    if (match(written))
                    {
                        return written;
                    }
                }

                await Task.Delay(TimeSpan.FromMilliseconds(10), cancellationToken);
            }
        }
    }
}
