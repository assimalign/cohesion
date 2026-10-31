using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;

using Xunit;

using Assimalign.Cohesion.Connections;
using Assimalign.Cohesion.Connections.InMemory;
using Assimalign.Cohesion.Connections.Tcp;
using Assimalign.Cohesion.Http;
using Assimalign.Cohesion.Http.Connections;
using Assimalign.Cohesion.Logging;
using Assimalign.Cohesion.Web.ErrorHandling;
using Assimalign.Cohesion.Web.Hosting.Tests.TestObjects;
using Assimalign.Cohesion.Web.Testing;

using CohesionHttpStatusCode = Assimalign.Cohesion.Http.HttpStatusCode;

namespace Assimalign.Cohesion.Web.Hosting.Tests;

/// <summary>
/// A peer abandoning an upload cancels the exchange through the transport, even when the handler
/// supplies no read cancellation token, and remains a cancellation through the exception boundary.
/// </summary>
[Collection(nameof(TelemetryCollection))]
public class RequestBodyCancellationTests
{
    [Theory(DisplayName = "Cohesion Test [Web.Hosting] - Request body: A lost connection or peer reset should cancel the exchange without an application fault")]
    [InlineData(WebApplicationTestProtocol.Http1, false)]
    [InlineData(WebApplicationTestProtocol.Http2, false)]
    [InlineData(WebApplicationTestProtocol.Http2, true)]
    public async Task ServerSpan_ConnectionOrStreamLostMidBody_ShouldReportRequestCanceledWithoutAnError(
        WebApplicationTestProtocol protocol,
        bool resetStream)
    {
        // Arrange — HTTP/1.1 uses a real TCP RST; HTTP/2 covers connection EOF and RST_STREAM.
        // The first read consumes exactly the five bytes sent, then a tokenless read waits for more.
        using CancellationTokenSource cancellation = new(TimeSpan.FromSeconds(30));
        CancellationToken cancellationToken = cancellation.Token;
        using TelemetryRecorder recorder = new(metrics: false);
        RecordingLoggerProvider recorded = new();
        TaskCompletionSource<bool> reading = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource<(Exception Failure, CancellationToken Token, bool Cancelled, CohesionHttpStatusCode? Fault)> failed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        int exceptionCallbacks = 0;
        string path = $"/telemetry/connection-lost-mid-body/{Guid.NewGuid():N}";

        await using TcpConnectionListener tcp = new(new TcpConnectionListenerOptions
        {
            EndPoint = new IPEndPoint(IPAddress.Loopback, 0),
        });
        await using InMemoryConnectionListener memory = new();
        await using WebApplicationTestFactory factory = new();
        factory.Builder.Logging.AddProvider(recorded);
        factory.Builder.Server.UseServer(options =>
        {
            if (protocol == WebApplicationTestProtocol.Http1)
            {
                options.UseHttp1(tcp);
            }
            else
            {
                options.UseHttp2(memory);
            }
        });
        factory.Builder.Services.AddErrorHandling(errors => errors.Options.OnException = (_, _) =>
        {
            Interlocked.Increment(ref exceptionCallbacks);
            return ValueTask.CompletedTask;
        });
        factory.Application.UseErrorHandling();
        factory.Application.Use(async (context, next) =>
        {
            IWebClientFaultFeature? clientFault = context.Features.Get<IWebClientFaultFeature>();
            CancellationToken requestToken = context.RequestCancelled;
            byte[] buffer = new byte[256];

            try
            {
                await context.Request.Body.ReadExactlyAsync(buffer.AsMemory(0, 5), CancellationToken.None);
                ValueTask<int> pendingRead = context.Request.Body.ReadAsync(buffer, CancellationToken.None);
                reading.TrySetResult(!pendingRead.IsCompleted);
                await pendingRead;
            }
            catch (Exception exception) when (exception is IOException or OperationCanceledException or ConnectionException or SocketException)
            {
                failed.TrySetResult((exception, requestToken, requestToken.IsCancellationRequested, clientFault?.StatusCode));
                throw;
            }
        });
        await factory.StartAsync(cancellationToken);

        using Socket? socket = protocol == WebApplicationTestProtocol.Http1
            ? new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true }
            : null;
        await using Http2RawClient? http2 = protocol == WebApplicationTestProtocol.Http2
            ? await Http2RawClient.ConnectAsync(memory, cancellationToken)
            : null;

        if (socket is not null)
        {
            await socket.ConnectAsync(tcp.EndPoint, cancellationToken);
            using NetworkStream stream = new(socket, ownsSocket: false);
            await stream.WriteAsync(Encoding.ASCII.GetBytes($"POST {path} HTTP/1.1\r\nHost: localhost\r\nContent-Length: 100\r\n\r\nhello"), cancellationToken);
            await stream.FlushAsync(cancellationToken);
        }
        else
        {
            await http2!.SendPostHeadersAsync(1, path, cancellationToken);
            await http2.SendDataAsync(1, Encoding.ASCII.GetBytes("hello"), endStream: false, cancellationToken);
        }

        (await reading.Task.WaitAsync(cancellationToken)).ShouldBeTrue();

        // Act — abandon only after the handler is awaiting its next body read.
        if (socket is not null)
        {
            socket.LingerState = new LingerOption(enable: true, seconds: 0);
            socket.Dispose();
        }
        else if (resetStream)
        {
            await http2!.SendResetAsync(1, errorCode: 0x8, cancellationToken);
        }
        else
        {
            await http2!.DisposeAsync();
        }

        (Exception failure, CancellationToken requestCancelled, bool cancelled, CohesionHttpStatusCode? fault) = await failed.Task.WaitAsync(cancellationToken);
        Activity span = await recorder.WaitForStoppedAsync(activity => Equals(activity.GetTagItem("url.path"), path), cancellationToken);
        await factory.StopAsync(cancellationToken);

        // Assert — the transport supplies the exchange token before the exception boundary sees it.
        OperationCanceledException canceled = failure.ShouldBeAssignableTo<OperationCanceledException>()!;
        cancelled.ShouldBeTrue();
        requestCancelled.IsCancellationRequested.ShouldBeTrue();
        canceled.CancellationToken.ShouldBe(requestCancelled);
        fault.ShouldBeNull();
        span.GetTagItem("error.type").ShouldBe("request_canceled");
        span.GetTagItem("http.response.status_code").ShouldBeNull();
        Volatile.Read(ref exceptionCallbacks).ShouldBe(0);
        recorded.Entries.ShouldNotContain(entry => entry.Level >= LogLevel.Error);
    }
}
