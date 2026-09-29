using System;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Logging;

namespace Assimalign.Cohesion.Hosting.Telemetry.Internal;

internal sealed class TelemetryHostService : IHostService
{
    private readonly OtlpLoggerProvider _provider;
    private int _stopped;
    private int _started;
    /// <summary>Initializes a new instance of the <see cref="TelemetryHostService"/> class.</summary>
    /// <param name="provider">The OTLP logger provider whose exporter the service flushes and disposes on stop.</param>
    public TelemetryHostService(OtlpLoggerProvider provider)
    {
        _provider = provider;
    }
    public ServiceId Id { get; } = ServiceId.New();
    internal ILoggerFactory? OwnedFactory { get; set; }
    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        Interlocked.Exchange(ref _started, 1);
        _provider.Write(new LoggerEntry(LogLevel.Information, "Cohesion.Hosting", "Resource telemetry started"));
        return Task.CompletedTask;
    }
    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        if (Interlocked.Exchange(ref _stopped, 1) != 0) { return; }
        if (Volatile.Read(ref _started) != 0)
        {
            _provider.Write(new LoggerEntry(LogLevel.Information, "Cohesion.Hosting", "Resource telemetry stopping"));
        }
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(TimeSpan.FromSeconds(5));
        try
        {
            await _provider.Exporter.FlushAsync(budget.Token).AsTask().WaitAsync(budget.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { }
        finally
        {
            // Begin teardown even when the host's remaining shutdown budget has already expired.
            Task disposal = _provider.Exporter.DisposeAsync().AsTask();
            Task cleanup = OwnedFactory is { } factory
                ? DisposeFactoryAsync(disposal, factory) : disposal;
            try
            {
                await cleanup.WaitAsync(budget.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { }
        }
    }

    private static async Task DisposeFactoryAsync(Task disposal, ILoggerFactory factory)
    {
        await disposal.ConfigureAwait(false);
        await Task.Run(factory.Dispose, CancellationToken.None).ConfigureAwait(false);
    }
}
