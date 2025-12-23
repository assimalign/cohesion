using System.Threading;

namespace Assimalign.Cohesion.Resilience;

#pragma warning disable RS0026 // Do not add multiple public overloads with optional parameters
#pragma warning disable CA1716 // Identifiers should not match keywords

/// <summary>
/// The pool of <see cref="ResilienceContextO"/> instances.
/// </summary>
public abstract partial class ResilienceContextPool
{
    /// <summary>
    /// Gets the shared pool instance.
    /// </summary>
    public static ResilienceContextPool Shared { get; } = new SharedPool();

    /// <summary>
    /// Gets a <see cref="ResilienceContextO"/> instance from the pool.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>An instance of <see cref="ResilienceContextO"/>.</returns>
    /// <remarks>
    /// After the execution is finished you should return the <see cref="ResilienceContextO"/> back to the pool
    /// by calling <see cref="Return(ResilienceContextO)"/> method.
    /// </remarks>
    public ResilienceContext Rent(CancellationToken cancellationToken = default) => Rent(null, cancellationToken);

    /// <summary>
    /// Gets a <see cref="ResilienceContextO"/> instance from the pool.
    /// </summary>
    /// <param name="operationKey">An operation key associated with the context.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>An instance of <see cref="ResilienceContextO"/>.</returns>
    /// <remarks>
    /// After the execution is finished you should return the <see cref="ResilienceContextO"/> back to the pool
    /// by calling <see cref="Return(ResilienceContextO)"/> method.
    /// </remarks>
    public ResilienceContext Rent(string? operationKey, CancellationToken cancellationToken = default) => Rent(operationKey, null, cancellationToken);

    /// <summary>
    /// Gets a <see cref="ResilienceContextO"/> instance from the pool.
    /// </summary>
    /// <param name="operationKey">An operation key associated with the context.</param>
    /// <param name="continueOnCapturedContext">Value indicating whether to continue on captured context.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>An instance of <see cref="ResilienceContextO"/>.</returns>
    /// <remarks>
    /// After the execution is finished you should return the <see cref="ResilienceContextO"/> back to the pool
    /// by calling <see cref="Return(ResilienceContextO)"/> method.
    /// </remarks>
    public ResilienceContext Rent(string? operationKey, bool? continueOnCapturedContext, CancellationToken cancellationToken = default)
        => Rent(new ResilienceContextCreationArguments(operationKey, continueOnCapturedContext, cancellationToken));

    /// <summary>
    /// Gets a <see cref="ResilienceContextO"/> instance from the pool.
    /// </summary>
    /// <param name="continueOnCapturedContext">Value indicating whether to continue on captured context.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>An instance of <see cref="ResilienceContextO"/>.</returns>
    /// <remarks>
    /// After the execution is finished you should return the <see cref="ResilienceContextO"/> back to the pool
    /// by calling <see cref="Return(ResilienceContextO)"/> method.
    /// </remarks>
    public ResilienceContext Rent(bool continueOnCapturedContext, CancellationToken cancellationToken = default)
        => Rent(new ResilienceContextCreationArguments(null, continueOnCapturedContext, cancellationToken));

    /// <summary>
    /// Gets a <see cref="ResilienceContextO"/> instance from the pool.
    /// </summary>
    /// <param name="arguments">The creation arguments.</param>
    /// <returns>An instance of <see cref="ResilienceContextO"/>.</returns>
    /// <remarks>
    /// After the execution is finished you should return the <see cref="ResilienceContextO"/> back to the pool
    /// by calling <see cref="Return(ResilienceContextO)"/> method.
    /// </remarks>
    public abstract ResilienceContext Rent(ResilienceContextCreationArguments arguments);

    /// <summary>
    /// Returns a <paramref name="context"/> back to the pool.
    /// </summary>
    /// <param name="context">The context instance.</param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="context"/> is <see langword="null"/>.</exception>
    public abstract void Return(ResilienceContext context);
}
