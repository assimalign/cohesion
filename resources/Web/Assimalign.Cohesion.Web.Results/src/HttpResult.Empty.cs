using System;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Web.Results;

using Assimalign.Cohesion.Http;

/// <summary>
/// A result that writes nothing at all: the response is left exactly as the pipeline shaped it.
/// Use it when a handler (or an earlier middleware) has already written the response imperatively
/// and the endpoint contract still requires an <see cref="IResult"/> to be returned.
/// </summary>
/// <remarks>
/// Created through <see cref="Results.Empty"/> or <see cref="TypedResults.Empty"/>; the constructor
/// is internal so the factories remain the only entry point. The type is stateless, so one shared
/// instance serves every exchange. Contrast with <see cref="NoContentHttpResult"/>, which
/// <em>does</em> mutate the response by setting <c>204</c>.
/// </remarks>
public sealed class EmptyHttpResult : IResult
{
    /// <summary>The shared, stateless instance.</summary>
    internal static EmptyHttpResult Instance { get; } = new();

    private EmptyHttpResult()
    {
    }

    /// <summary>
    /// Does nothing: the response keeps whatever status, headers, and body it already has.
    /// </summary>
    /// <param name="context">The HTTP exchange; left untouched.</param>
    /// <param name="cancellationToken">Ignored; the result performs no work.</param>
    /// <returns>A completed task.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="context"/> is <see langword="null"/>.</exception>
    public Task ExecuteAsync(IHttpContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        return Task.CompletedTask;
    }
}
