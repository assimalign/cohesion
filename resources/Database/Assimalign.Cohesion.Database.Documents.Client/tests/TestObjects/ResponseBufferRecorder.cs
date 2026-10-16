using System;
using System.Diagnostics.Tracing;
using System.Linq;

namespace Assimalign.Cohesion.Database.Documents.Client.Tests;

internal sealed class ResponseBufferRecorder : EventListener
{
    private long _materializedBytes;

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
    internal long PeakBufferBytes { get; private set; }

    protected override void OnEventWritten(EventWrittenEventArgs eventData)
    {
        if (eventData.EventSource.Name == "Assimalign.Cohesion.Database" && eventData.EventName == "StatementStop")
        {
            // The engine has already materialized the complete result. Subsequent live bytes
            // measure the response's additional buffering, rather than engine row storage.
            _materializedBytes = GC.GetTotalMemory(forceFullCollection: true);
            BaselineCount++;
        }
        else if (eventData.EventSource.Name == "Assimalign.Cohesion.Database.Protocol" &&
            eventData.EventName == "FrameWritten" && eventData.Payload?[0] is string type &&
            type is "65" or "66" or "67" or "68")
        {
            FrameCount++;
            if (type is "65" or "68") { RowCount++; }
            PeakFrameBytes = Math.Max(PeakFrameBytes, (int)eventData.Payload[1]!);
            if (type is "67" or "66" || RowCount % 16 == 0)
            {
                PeakBufferBytes = Math.Max(PeakBufferBytes,
                    GC.GetTotalMemory(forceFullCollection: true) - _materializedBytes);
            }
        }
    }
}
