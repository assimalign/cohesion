using System.Threading;

namespace Assimalign.Cohesion.Web.Hosting.Internal;

/// <summary>
/// Publishes the <see cref="WebApplicationServer"/>'s drain signal to its exchanges.
/// </summary>
/// <remarks>
/// The token never changes for the life of the server, so one instance serves every exchange: the
/// server installs it beside the response-completion feature before the pipeline runs, and no
/// exchange allocates for it. The token is captured once from
/// <see cref="WebApplicationServerDrain.Draining"/>, so it stays readable after the server disposes
/// its sources at the end of a stop.
/// </remarks>
internal sealed class WebServerDrainFeature : IWebServerDrainFeature
{
    public WebServerDrainFeature(CancellationToken draining)
    {
        Draining = draining;
    }

    /// <inheritdoc />
    public string Name => nameof(WebServerDrainFeature);

    /// <inheritdoc />
    public CancellationToken Draining { get; }
}
