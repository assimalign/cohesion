using System;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Web.Authorization.Internal;

/// <summary>
/// Requires a delegate over the evaluation context to return <see langword="true"/>: the AOT-safe
/// custom requirement behind <see cref="AuthorizationPolicyBuilder.RequireAssertion(Func{AuthorizationContext, bool})"/>
/// and its asynchronous overload. The delegate is captured at builder time and invoked directly, with
/// no reflection or activation.
/// </summary>
internal sealed class AssertionRequirement : IAuthorizationRequirement
{
    private readonly Func<AuthorizationContext, bool>? _assertion;
    private readonly Func<AuthorizationContext, CancellationToken, ValueTask<bool>>? _asyncAssertion;

    /// <summary>
    /// Creates a requirement over a synchronous assertion.
    /// </summary>
    /// <param name="assertion">The assertion.</param>
    /// <exception cref="ArgumentNullException"><paramref name="assertion"/> is <see langword="null"/>.</exception>
    public AssertionRequirement(Func<AuthorizationContext, bool> assertion)
    {
        ArgumentNullException.ThrowIfNull(assertion);

        _assertion = assertion;
    }

    /// <summary>
    /// Creates a requirement over an asynchronous assertion.
    /// </summary>
    /// <param name="assertion">The assertion.</param>
    /// <exception cref="ArgumentNullException"><paramref name="assertion"/> is <see langword="null"/>.</exception>
    public AssertionRequirement(Func<AuthorizationContext, CancellationToken, ValueTask<bool>> assertion)
    {
        ArgumentNullException.ThrowIfNull(assertion);

        _asyncAssertion = assertion;
    }

    /// <inheritdoc />
    public ValueTask<bool> EvaluateAsync(AuthorizationContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        return _assertion is not null
            ? new ValueTask<bool>(_assertion.Invoke(context))
            : _asyncAssertion!.Invoke(context, cancellationToken);
    }
}
