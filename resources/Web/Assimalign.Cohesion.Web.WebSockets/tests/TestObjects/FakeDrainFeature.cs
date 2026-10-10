using System;
using System.Threading;

namespace Assimalign.Cohesion.Web.WebSockets.Tests.TestObjects;

/// <summary>
/// The server's drain signal, under the test's control: <see cref="BeginDrain"/> does what the
/// default server's stop does to <see cref="IWebServerDrainFeature.Draining"/>.
/// </summary>
internal sealed class FakeDrainFeature : IWebServerDrainFeature, IDisposable
{
    private readonly CancellationTokenSource _draining = new();

    public string Name => "WebServerDrainFeature";

    public CancellationToken Draining => _draining.Token;

    public void BeginDrain() => _draining.Cancel();

    public void Dispose() => _draining.Dispose();
}
