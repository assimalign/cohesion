using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics.Metrics;
using System.Linq;

namespace Assimalign.Cohesion.Http.Connections.Tests;

/// <summary>Records the transport's counter measurements and their bounded tags.</summary>
internal sealed class TransportMeterRecorder : IDisposable
{
    private readonly ConcurrentQueue<Measurement> _measurements = new();
    private readonly MeterListener _listener;

    public TransportMeterRecorder()
    {
        _listener = new MeterListener
        {
            InstrumentPublished = (instrument, listener) =>
            {
                if (instrument.Meter.Name == "Assimalign.Cohesion.Http.Connections")
                {
                    listener.EnableMeasurementEvents(instrument);
                }
            }
        };
        _listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) =>
        {
            Dictionary<string, object?> recordedTags = new(StringComparer.Ordinal);
            foreach (KeyValuePair<string, object?> tag in tags)
            {
                recordedTags.Add(tag.Key, tag.Value);
            }

            _measurements.Enqueue(new Measurement(instrument.Name, instrument.Unit, value, recordedTags));
        });
        _listener.Start();
    }

    public IReadOnlyList<Measurement> Measurements => _measurements.ToArray();

    public long Count(string instrumentName, string protocol, int? statusCode = null, string? errorType = null, string? scope = null)
        => _measurements.Where(measurement => measurement.Name == instrumentName &&
            Equals(measurement.Tags["network.protocol.version"], protocol) &&
            (statusCode is null || Equals(measurement.Tags["cohesion.http.rejection.status_code"], statusCode.Value)) &&
            (errorType is null || Equals(measurement.Tags["error.type"], errorType)) &&
            (scope is null || Equals(measurement.Tags["cohesion.http.error.scope"], scope)))
            .Sum(measurement => measurement.Value);

    public void Dispose() => _listener.Dispose();

    internal sealed record Measurement(string Name, string? Unit, long Value, IReadOnlyDictionary<string, object?> Tags);
}
