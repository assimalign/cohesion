using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Web.Authorization;

/// <summary>
/// An immutable authorization policy: the requirements a request's principal must satisfy, and the
/// authentication schemes that establish that principal.
/// </summary>
/// <remarks>
/// <para>
/// A policy authorizes a request when <em>every</em> requirement is satisfied. Requirements are
/// evaluated in order and evaluation stops at the first unsatisfied one. Build a policy with
/// <see cref="AuthorizationPolicyBuilder"/>, register it by name with
/// <see cref="AuthorizationOptions.AddPolicy(string, AuthorizationPolicy)"/>, or attach it to an
/// endpoint inline with <c>RequireAuthorization(policy)</c>.
/// </para>
/// <para>
/// <see cref="AuthenticationSchemes"/> selects the principal. When it is empty, the policy evaluates
/// <c>context.User</c>, which <c>UseAuthentication</c> established from the default authenticate
/// scheme. When it names schemes, <c>UseAuthorization</c> authenticates the request with each of them,
/// evaluates the policy against their combined principal, replaces <c>context.User</c> with that
/// principal, and challenges or forbids through those schemes when the policy fails. That is how an
/// endpoint selects its own schemes: a Bearer-only API endpoint ignores a cookie the default scheme
/// accepted.
/// </para>
/// <para>
/// A policy is a builder-time value and is shared by every request it governs. It is immutable once
/// constructed: its lists are read-only copies, and its requirements must be thread-safe.
/// </para>
/// </remarks>
public sealed class AuthorizationPolicy
{
    private readonly IAuthorizationRequirement[] _requirements;

    /// <summary>
    /// Creates a policy from its requirements and the authentication schemes it evaluates.
    /// </summary>
    /// <param name="requirements">The requirements, in evaluation order. At least one is required.</param>
    /// <param name="authenticationSchemes">
    /// The authentication schemes that establish the principal, or <see langword="null"/> (or empty) to
    /// evaluate <c>context.User</c>. Duplicates are removed (ordinal); the first occurrence keeps its place.
    /// </param>
    /// <exception cref="ArgumentNullException"><paramref name="requirements"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="requirements"/> is empty or contains a <see langword="null"/> entry, or
    /// <paramref name="authenticationSchemes"/> contains a <see langword="null"/>, empty, or whitespace entry.
    /// </exception>
    public AuthorizationPolicy(IEnumerable<IAuthorizationRequirement> requirements, IEnumerable<string>? authenticationSchemes = null)
    {
        ArgumentNullException.ThrowIfNull(requirements);

        List<IAuthorizationRequirement> copy = new(requirements);

        if (copy.Count == 0)
        {
            throw new ArgumentException(
                "An authorization policy needs at least one requirement; a policy with none would authorize every request.",
                nameof(requirements));
        }

        for (int i = 0; i < copy.Count; i++)
        {
            if (copy[i] is null)
            {
                throw new ArgumentException("Authorization requirements must not be null.", nameof(requirements));
            }
        }

        _requirements = copy.ToArray();
        Requirements = Array.AsReadOnly(_requirements);
        AuthenticationSchemes = CopySchemes(authenticationSchemes);
    }

    /// <summary>
    /// Gets the requirements, in evaluation order. Never empty.
    /// </summary>
    public IReadOnlyList<IAuthorizationRequirement> Requirements { get; }

    /// <summary>
    /// Gets the authentication schemes that establish the principal the policy evaluates, in the order
    /// they authenticate and challenge. Empty when the policy evaluates <c>context.User</c>.
    /// </summary>
    public IReadOnlyList<string> AuthenticationSchemes { get; }

    /// <summary>
    /// Evaluates every requirement against <paramref name="context"/>, in order, stopping at the first
    /// unsatisfied one.
    /// </summary>
    /// <remarks>
    /// This is the evaluation <c>UseAuthorization</c> runs for an endpoint. Call it directly for an
    /// imperative check inside a handler, for example against <c>context.User</c>. It only evaluates:
    /// it neither authenticates the policy's schemes nor answers a failure.
    /// </remarks>
    /// <param name="context">The principal to evaluate and the exchange it made.</param>
    /// <param name="cancellationToken">A token that cancels the evaluation.</param>
    /// <returns><see langword="true"/> when every requirement is satisfied; otherwise <see langword="false"/>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="context"/> is <see langword="null"/>.</exception>
    public ValueTask<bool> EvaluateAsync(AuthorizationContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        for (int i = 0; i < _requirements.Length; i++)
        {
            ValueTask<bool> pending = _requirements[i].EvaluateAsync(context, cancellationToken);

            if (!pending.IsCompletedSuccessfully)
            {
                return EvaluateRemainingAsync(pending, i, context, cancellationToken);
            }

            if (!pending.Result)
            {
                return new ValueTask<bool>(false);
            }
        }

        return new ValueTask<bool>(true);
    }

    // The slow path, entered at the first requirement that does not complete synchronously. The
    // built-in requirements other than an asynchronous assertion always complete synchronously, so a
    // typical policy never allocates a state machine.
    private async ValueTask<bool> EvaluateRemainingAsync(
        ValueTask<bool> pending,
        int index,
        AuthorizationContext context,
        CancellationToken cancellationToken)
    {
        if (!await pending.ConfigureAwait(false))
        {
            return false;
        }

        for (int i = index + 1; i < _requirements.Length; i++)
        {
            if (!await _requirements[i].EvaluateAsync(context, cancellationToken).ConfigureAwait(false))
            {
                return false;
            }
        }

        return true;
    }

    private static ReadOnlyCollection<string> CopySchemes(IEnumerable<string>? authenticationSchemes)
    {
        if (authenticationSchemes is null)
        {
            return ReadOnlyCollection<string>.Empty;
        }

        List<string> schemes = new();
        HashSet<string> seen = new(StringComparer.Ordinal);

        foreach (string scheme in authenticationSchemes)
        {
            if (string.IsNullOrWhiteSpace(scheme))
            {
                throw new ArgumentException(
                    "Authentication scheme names must not be null, empty, or whitespace.",
                    nameof(authenticationSchemes));
            }

            if (seen.Add(scheme))
            {
                schemes.Add(scheme);
            }
        }

        return schemes.Count == 0 ? ReadOnlyCollection<string>.Empty : schemes.AsReadOnly();
    }
}
