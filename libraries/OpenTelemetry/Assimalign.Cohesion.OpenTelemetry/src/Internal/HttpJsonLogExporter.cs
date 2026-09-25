using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.OpenTelemetry.Internal;

internal sealed class HttpJsonLogExporter : IOtlpLogExporter
{
    private readonly Channel<OtlpLogRecord> _queue;
    private readonly HttpClient _client;
    private readonly Uri _endpoint;
    private readonly Dictionary<string, string> _headers;
    private readonly Dictionary<string, string> _attributes;
    private readonly TimeSpan _timeout;
    private readonly int _batchSize;
    private readonly SemaphoreSlim _drain = new(1, 1);
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _worker;
    private readonly object _disposeLock = new();
    private Task? _disposal;
    private long _dropped;
    private long _failed;
    private int _disposed;

    internal HttpJsonLogExporter(OtlpExporterOptions options)
    {
        _endpoint = new Uri(options.Endpoint.AbsoluteUri.TrimEnd('/') + "/v1/logs");
        _headers = new Dictionary<string, string>(options.Headers, StringComparer.OrdinalIgnoreCase);
        _attributes = new Dictionary<string, string>(options.ResourceAttributes, StringComparer.Ordinal);
        // Validate before creating any disposable transport or background task.
        using (var request = new HttpRequestMessage())
        {
            foreach (var header in _headers)
            {
                request.Headers.Add(header.Key, header.Value);
            }
        }

        _timeout = options.Timeout;
        _batchSize = Math.Min(options.MaxBatchSize, options.MaxQueueLength);
        _queue = Channel.CreateBounded<OtlpLogRecord>(new BoundedChannelOptions(options.MaxQueueLength)
        {
            FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true, AllowSynchronousContinuations = false
        }, _ => Interlocked.Increment(ref _dropped));
        _client = new HttpClient(options.HandlerFactory?.Invoke() ?? new SocketsHttpHandler
        {
            AllowAutoRedirect = false, UseCookies = false
        }, disposeHandler: true) { Timeout = Timeout.InfiniteTimeSpan };
        TimeSpan interval = options.FlushInterval;
        _worker = Task.Run(() => DrainLoopAsync(interval));
    }

    public long DroppedCount => Interlocked.Read(ref _dropped);
    public long FailedExportCount => Interlocked.Read(ref _failed);

    public bool TryEnqueue(OtlpLogRecord record)
    {
        if (record is not null && Volatile.Read(ref _disposed) == 0 && _queue.Writer.TryWrite(record))
        {
            return true;
        }

        Interlocked.Increment(ref _dropped);
        return false;
    }

    public async ValueTask<bool> ExportAsync(ReadOnlyMemory<OtlpLogRecord> batch, CancellationToken cancellationToken = default)
    {
        if (batch.IsEmpty)
        {
            return true;
        }

        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(_timeout);
        try
        {
            byte[] payload = OtlpJsonEncoder.Encode(batch, _attributes);
            for (int attempt = 0; attempt < 3; attempt++)
            {
                TimeSpan delay = TimeSpan.FromMilliseconds(100 * (1 << attempt));
                bool acceptedStatus = false;
                try
                {
                    using var attemptBudget = CancellationTokenSource.CreateLinkedTokenSource(budget.Token);
                    attemptBudget.CancelAfter(TimeSpan.FromTicks(Math.Max(1, _timeout.Ticks / 3)));
                    using var request = new HttpRequestMessage(HttpMethod.Post, _endpoint);
                    foreach (var header in _headers)
                    {
                        request.Headers.Add(header.Key, header.Value);
                    }

                    request.Content = new ByteArrayContent(payload);
                    request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
                    using HttpResponseMessage response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, attemptBudget.Token).ConfigureAwait(false);
                    if (response.IsSuccessStatusCode)
                    {
                        acceptedStatus = true;
                        // Bound response buffering too: a collector is outside the process trust boundary.
                        await response.Content.LoadIntoBufferAsync(65536, attemptBudget.Token).ConfigureAwait(false);
                        byte[] body = await response.Content.ReadAsByteArrayAsync(attemptBudget.Token).ConfigureAwait(false);
                        if (body.Length == 0)
                        {
                            return true;
                        }

                        using JsonDocument document = JsonDocument.Parse(body);
                        if (!document.RootElement.TryGetProperty("partialSuccess", out var partial) ||
                            !partial.TryGetProperty("rejectedLogRecords", out var rejected) || rejected.ToString() == "0")
                        {
                            return true;
                        }

                        Interlocked.Increment(ref _failed);
                        return false;
                    }
                    Interlocked.Increment(ref _failed);
                    if ((int)response.StatusCode < 500 && response.StatusCode != HttpStatusCode.TooManyRequests)
                    {
                        return false;
                    }

                    if (response.Headers.RetryAfter is { } retry)
                    {
                        TimeSpan requested = retry.Delta ?? (retry.Date!.Value - DateTimeOffset.UtcNow);
                        if (requested > delay)
                        {
                            delay = requested;
                        }
                    }
                }
                catch (HttpRequestException)
                {
                    Interlocked.Increment(ref _failed);
                    if (acceptedStatus) { return false; }
                }
                catch (OperationCanceledException) when (!budget.IsCancellationRequested)
                {
                    Interlocked.Increment(ref _failed);
                    if (acceptedStatus) { return false; }
                }
                if (attempt != 2)
                {
                    await Task.Delay(delay, budget.Token).ConfigureAwait(false);
                }
            }
        }
        // Isolation boundary: user-supplied attribute rendering/handlers can throw arbitrary exceptions.
        // Never allow diagnostics to take down the producer, including during host shutdown.
        catch (Exception) { Interlocked.Increment(ref _failed); }
        return false;
    }

    public async ValueTask FlushAsync(CancellationToken cancellationToken = default)
    {
        bool entered = false;
        try
        {
            await _drain.WaitAsync(cancellationToken).ConfigureAwait(false);
            entered = true;
            var batch = new OtlpLogRecord[_batchSize];
            while (!cancellationToken.IsCancellationRequested)
            {
                int count = 0;
                while (count < batch.Length && _queue.Reader.TryRead(out OtlpLogRecord? record))
                {
                    batch[count++] = record;
                }

                if (count == 0)
                {
                    break;
                }

                await ExportAsync(batch.AsMemory(0, count), cancellationToken).ConfigureAwait(false);
                Array.Clear(batch);
            }
        }
        catch (OperationCanceledException) { }
        finally { if (entered)
            {
                _drain.Release();
            }
        }
    }

    private async Task DrainLoopAsync(TimeSpan interval)
    {
        try
        {
            using var timer = new PeriodicTimer(interval);
            while (await timer.WaitForNextTickAsync(_stop.Token).ConfigureAwait(false))
            {
                await FlushAsync(_stop.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { }
    }

    public ValueTask DisposeAsync()
    {
        lock (_disposeLock)
        {
            return new ValueTask(_disposal ??= DisposeCoreAsync());
        }
    }

    private async Task DisposeCoreAsync()
    {
        Interlocked.Exchange(ref _disposed, 1);
        _queue.Writer.TryComplete();
        await _stop.CancelAsync().ConfigureAwait(false);
        using var budget = new CancellationTokenSource(_timeout);
        await FlushAsync(budget.Token).ConfigureAwait(false);
        try { await _worker.WaitAsync(budget.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) { }
        _client.Dispose();
        while (_queue.Reader.TryRead(out _))
        {
            Interlocked.Increment(ref _dropped);
        }
    }
}
