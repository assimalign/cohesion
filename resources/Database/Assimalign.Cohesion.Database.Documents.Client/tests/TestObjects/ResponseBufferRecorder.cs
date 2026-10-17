using System;
using System.Diagnostics.Tracing;
using System.Linq;

namespace Assimalign.Cohesion.Database.Documents.Client.Tests;

internal sealed class ResponseBufferRecorder : EventListener
{
    private long _allocatedAtMaterialization;

    internal ResponseBufferRecorder()
    {
        foreach (var source in EventSource.GetSources().Where(source => source.Name is
            "Assimalign.Cohesion.Database" or "Assimalign.Cohesion.Database.Protocol"))
        {
            EnableEvents(source, EventLevel.Verbose, EventKeywords.All);
        }
    }

    internal int BaselineCount { get; private set; }
    internal int FrameCount { get; private set; }
    internal int RowCount { get; private set; }
    internal int PeakFrameBytes { get; private set; }
    internal long BytesAllocatedBeforeHeader { get; private set; }

    protected override void OnEventWritten(EventWrittenEventArgs eventData)
    {
        if (eventData.EventSource.Name == "Assimalign.Cohesion.Database" && eventData.EventName == "StatementStop")
        {
            // Count allocations between engine materialization and the first response frame.
            // A pre-encoded result allocates its entire copy in this window. Whole-process
            // live heap deltas also count client frames and engine storage/pool growth.
            _allocatedAtMaterialization = GC.GetTotalAllocatedBytes(precise: true);
            BaselineCount++;
        }
        else if (eventData.EventSource.Name == "Assimalign.Cohesion.Database.Protocol" &&
            eventData.EventName == "FrameWritten" && eventData.Payload?[0] is string type &&
            type is "65" or "66" or "67" or "68")
        {
            FrameCount++;
            if (type is "65" or "68") { RowCount++; }
            PeakFrameBytes = Math.Max(PeakFrameBytes, (int)eventData.Payload[1]!);
            if (type == "67")
            {
                BytesAllocatedBeforeHeader = GC.GetTotalAllocatedBytes(precise: true) - _allocatedAtMaterialization;
            }
        }
    }
}
