using System;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Logging;

namespace Assimalign.Cohesion.Hosting.Telemetry;

internal sealed class TelemetryHostService(OtlpLoggerProvider provider) : IHostService
{
    private int _stopped;
    private int _started;
    public ServiceId Id { get; } = ServiceId.New();
    internal ILoggerFactory? OwnedFactory { get; set; }
    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        Interlocked.Exchange(ref _started, 1);
        provider.Write(new LoggerEntry(LogLevel.Information, "Cohesion.Hosting", "Resource telemetry started"));
        return Task.CompletedTask;
    }
    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        if (Interlocked.Exchange(ref _stopped, 1) != 0) { return; }
        if (Volatile.Read(ref _started) != 0)
        {
            provider.Write(new LoggerEntry(LogLevel.Information, "Cohesion.Hosting", "Resource telemetry stopping"));
        }
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(TimeSpan.FromSeconds(5));
        try
        {
            await provider.Exporter.FlushAsync(budget.Token).AsTask().WaitAsync(budget.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { }
        finally
        {
            // Begin teardown even when the host's remaining shutdown budget has already expired.
            Task disposal = provider.Exporter.DisposeAsync().AsTask();
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
