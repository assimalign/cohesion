using System;
using System.Threading.Tasks;

using Assimalign.Cohesion.Http;

namespace Assimalign.Cohesion.Web;

/// <summary>Registers work to run after the response has been written to the transport.</summary>
/// <remarks>
/// Callbacks run in registration order. The default Web server installs this feature on every
/// exchange. A custom <see cref="IWebApplicationServer"/> may omit it, so consumers must handle
/// an absent feature.
/// </remarks>
public interface IWebResponseCompletionFeature : IHttpFeature
{
    /// <summary>Registers a callback to run after response transmission completes.</summary>
    /// <param name="callback">The asynchronous completion callback.</param>
    /// <exception cref="ArgumentNullException"><paramref name="callback"/> is null.</exception>
    /// <exception cref="InvalidOperationException">The response has already completed.</exception>
    void Register(Func<ValueTask> callback);
}
