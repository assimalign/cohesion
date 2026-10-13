using System;
using System.Net.Quic;
using System.Runtime.Versioning;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;

using Xunit;

using Assimalign.Cohesion.Connections;
using Assimalign.Cohesion.Web.Internal;
using Assimalign.Cohesion.Web.Server.Tests.TestObjects;

namespace Assimalign.Cohesion.Web.Server.Tests;

/// <summary>
/// Exercises the server's drain lifetime against a real QUIC stream whose peer withholds delivery credit.
/// </summary>
[SupportedOSPlatform("windows")]
[SupportedOSPlatform("linux")]
[SupportedOSPlatform("macos")]
public class WebApplicationServerQuicDrainTests
{
    private static readonly TimeSpan _timeout = TimeSpan.FromSeconds(30);

    [Theory(DisplayName = "Cohesion Test [Web.Server] - Server/Drain: The stop budget aborts QUIC delivery while connection disposal is waiting for the peer")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StopAsync_WhenQuicDeliveryOutlivesBudget_ShouldAbortAndFinishConnectionDisposal(bool receiveEndsNaturally)
    {
        if (!QuicListener.IsSupported)
        {
            return;
        }

        // Arrange — the peer never reads. Its small flow-control window leaves the response's tail
        // and FIN undelivered, so QUIC disposal would wait for its five-second delivery timeout.
        using CancellationTokenSource cancellation = new(_timeout);
        using CancellationTokenSource budget = new();
        using X509Certificate2 certificate = SelfSignedCertificateFactory.Create("localhost");
        WithheldQuicDeliveryConnection connection = await WithheldQuicDeliveryConnection.CreateAsync(
            certificate, TimeSpan.FromSeconds(5), receiveEndsNaturally, cancellation.Token);
        FakeHttpConnectionListener listener = new(connection);
        WebApplicationServer server = new(new WebApplicationServerOptions
        {

            Pipeline = new FakePipeline(static (_, _) => Task.CompletedTask),
        }, listener);

        try
        {
            await server.StartAsync(cancellation.Token);
            await connection.Opened.WaitAsync(cancellation.Token);

            if (receiveEndsNaturally)
            {
                // The HTTP context has already been disposed when the stop starts. Only the abort
                // registration should remain active during transport disposal, not graceful close.
                await connection.DisposeStarted.WaitAsync(cancellation.Token);
            }

            Task stop = server.StopAsync(budget.Token);
            await connection.DisposeStarted.WaitAsync(cancellation.Token);
            connection.Disposed.IsCompleted.ShouldBeFalse();

            // Act — expire the shorter host budget after disposal has entered its delivery wait.
            budget.CancelAfter(TimeSpan.FromMilliseconds(250));

            // Assert — StopAsync alone is insufficient: its abort grace permits uncooperative work
            // to outlive it. The actual QUIC connection must finish disposal before its delivery timeout.
            await connection.Disposed.WaitAsync(TimeSpan.FromSeconds(2));
            await stop.WaitAsync(cancellation.Token);
            connection.AbortCount.ShouldBe(1);
            connection.State.ShouldBe(ConnectionState.Aborted);
            connection.GracefulCloseCount.ShouldBe(receiveEndsNaturally ? 0 : 1);
            listener.DisposeCount.ShouldBe(1);
        }
        finally
        {
            budget.Cancel();

            try
            {
                // The raw peer always closes first, including on a failed regression assertion,
                // so no delivery or server disposal task can survive this test.
                await connection.ReleaseAsync();
            }
            finally
            {
                await server.StopAsync(new CancellationToken(canceled: true)).WaitAsync(_timeout);
            }
        }
    }
}
