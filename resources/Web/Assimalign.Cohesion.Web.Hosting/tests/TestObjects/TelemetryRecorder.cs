using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Web.Hosting.Tests.TestObjects;

/// <summary>
/// One measurement a <see cref="TelemetryRecorder"/> observed.
/// </summary>
/// <param name="Instrument">The instrument's name.</param>
/// <param name="Value">The measured value.</param>
/// <param name="Tags">The measurement's attributes.</param>
internal sealed record RecordedMeasurement(string Instrument, double Value, IReadOnlyDictionary<string, object?> Tags);

/// <summary>
/// Subscribes to the server's <c>Assimalign.Cohesion.Web.Hosting</c> activity source and meter, and
/// records every stopped activity and every measurement until it is disposed.
/// </summary>
/// <remarks>
/// Listeners are process-wide, so the tests that use a recorder run in <see cref="TelemetryCollection"/>,
/// and each test still selects its own spans and measurements by an attribute only its requests carry.
/// </remarks>
internal sealed class TelemetryRecorder : IDisposable
{
    public const string SourceName = "Assimalign.Cohesion.Web.Hosting";

    private readonly ActivityListener? _activityListener;
    private readonly MeterListener? _meterListener;
    private readonly Lock _gate = new();
    private readonly List<Activity> _stopped = [];
    private readonly List<RecordedMeasurement> _measurements = [];
    private readonly List<(Func<Activity, bool> Match, TaskCompletionSource<Activity> Completion)> _activityWaiters = [];
    private readonly List<(Func<RecordedMeasurement, bool> Match, TaskCompletionSource<RecordedMeasurement> Completion)> _measurementWaiters = [];

    public TelemetryRecorder(bool traces = true, bool metrics = true)
    {
        if (traces)
        {
            _activityListener = new ActivityListener
            {
                ShouldListenTo = source => source.Name == SourceName,
                Sample = static (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
                ActivityStopped = OnActivityStopped,
            };

            ActivitySource.AddActivityListener(_activityListener);
        }

        if (metrics)
        {
            _meterListener = new MeterListener
            {
                InstrumentPublished = static (instrument, listener) =>
                {
                    if (instrument.Meter.Name == SourceName)
                    {
                        listener.EnableMeasurementEvents(instrument);
                    }
                },
            };

            _meterListener.SetMeasurementEventCallback<double>((instrument, value, tags, _) => OnMeasurement(instrument, value, tags));
            _meterListener.SetMeasurementEventCallback<long>((instrument, value, tags, _) => OnMeasurement(instrument, value, tags));
            _meterListener.Start();
        }
    }

    /// <summary>
    /// Gets a snapshot of the activities stopped so far.
    /// </summary>
    public IReadOnlyList<Activity> Stopped
    {
        get
        {
            lock (_gate)
            {
                return _stopped.ToArray();
            }
        }
    }

    /// <summary>
    /// Gets a snapshot of the measurements recorded so far.
    /// </summary>
    public IReadOnlyList<RecordedMeasurement> Measurements
    {
        get
        {
            lock (_gate)
            {
                return _measurements.ToArray();
            }
        }
    }

    /// <summary>
    /// Waits for a stopped activity that satisfies <paramref name="match"/>, including one that stopped
    /// before the call. The server stops an exchange's span after the response is sent, so a client can
    /// hold the response before the span has ended.
    /// </summary>
    public Task<Activity> WaitForStoppedAsync(Func<Activity, bool> match, CancellationToken cancellationToken)
    {
        TaskCompletionSource<Activity> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);

        lock (_gate)
        {
            if (_stopped.FirstOrDefault(match) is { } stopped)
            {
                return Task.FromResult(stopped);
            }

            _activityWaiters.Add((match, completion));
        }

        return completion.Task.WaitAsync(cancellationToken);
    }

    /// <summary>
    /// Waits for a measurement that satisfies <paramref name="match"/>, including one recorded before the
    /// call.
    /// </summary>
    public Task<RecordedMeasurement> WaitForMeasurementAsync(Func<RecordedMeasurement, bool> match, CancellationToken cancellationToken)
    {
        TaskCompletionSource<RecordedMeasurement> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);

        lock (_gate)
        {
            if (_measurements.FirstOrDefault(match) is { } measurement)
            {
                return Task.FromResult(measurement);
            }

            _measurementWaiters.Add((match, completion));
        }

        return completion.Task.WaitAsync(cancellationToken);
    }

    public void Dispose()
    {
        _activityListener?.Dispose();
        _meterListener?.Dispose();
    }

    private void OnActivityStopped(Activity activity)
    {
        lock (_gate)
        {
            _stopped.Add(activity);

            for (int i = _activityWaiters.Count - 1; i >= 0; i--)
            {
                if (_activityWaiters[i].Match(activity))
                {
                    _activityWaiters[i].Completion.TrySetResult(activity);
                    _activityWaiters.RemoveAt(i);
                }
            }
        }
    }

    private void OnMeasurement(Instrument instrument, double value, ReadOnlySpan<KeyValuePair<string, object?>> tags)
    {
        Dictionary<string, object?> attributes = new(StringComparer.Ordinal);

        foreach (KeyValuePair<string, object?> tag in tags)
        {
            attributes[tag.Key] = tag.Value;
        }

        RecordedMeasurement measurement = new(instrument.Name, value, attributes);

        lock (_gate)
        {
            _measurements.Add(measurement);

            for (int i = _measurementWaiters.Count - 1; i >= 0; i--)
            {
                if (_measurementWaiters[i].Match(measurement))
                {
                    _measurementWaiters[i].Completion.TrySetResult(measurement);
                    _measurementWaiters.RemoveAt(i);
                }
            }
        }
    }
}
