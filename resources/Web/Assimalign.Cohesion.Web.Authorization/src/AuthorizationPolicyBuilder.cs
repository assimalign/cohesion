using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Web.Authorization.Internal;

namespace Assimalign.Cohesion.Web.Authorization;

/// <summary>
/// Composes an <see cref="AuthorizationPolicy"/>: its requirements, in evaluation order, and the
/// authentication schemes that establish the principal it evaluates.
/// </summary>
/// <remarks>
/// <para>
/// Every <c>Require*</c> member appends a requirement, and a request must satisfy all of them.
/// <see cref="Build"/> snapshots the builder into an immutable policy and may be called more than
/// once. A policy needs at least one requirement: a scheme-only policy would authorize every request,
/// so <see cref="Build"/> rejects it. To select schemes for an endpoint that only needs a signed-in
/// user, pair <see cref="AddAuthenticationSchemes"/> with <see cref="RequireAuthenticatedUser"/>.
/// </para>
/// <para>
/// The builder is a composition-time object and is not thread-safe.
/// </para>
/// </remarks>
public sealed class AuthorizationPolicyBuilder
{
    private readonly List<IAuthorizationRequirement> _requirements = new();
    private readonly List<string> _authenticationSchemes = new();

    /// <summary>
    /// Adds authentication schemes the policy evaluates. When a policy names schemes,
    /// <c>UseAuthorization</c> authenticates the request with each of them, evaluates their combined
    /// principal instead of <c>context.User</c>, and challenges or forbids through them.
    /// </summary>
    /// <param name="schemes">The scheme names, as registered with <c>AddAuthentication</c>.</param>
    /// <returns>The same builder, for chaining.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="schemes"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="schemes"/> contains a <see langword="null"/>, empty, or whitespace entry.</exception>
    public AuthorizationPolicyBuilder AddAuthenticationSchemes(params string[] schemes)
    {
        _authenticationSchemes.AddRange(AuthorizationNames.Copy(schemes, nameof(schemes), "authentication scheme"));
        return this;
    }

    /// <summary>
    /// Adds custom requirements, evaluated after the requirements already added.
    /// </summary>
    /// <param name="requirements">The requirements.</param>
    /// <returns>The same builder, for chaining.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="requirements"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="requirements"/> contains a <see langword="null"/> entry.</exception>
    public AuthorizationPolicyBuilder AddRequirements(params IAuthorizationRequirement[] requirements)
    {
        ArgumentNullException.ThrowIfNull(requirements);

        foreach (IAuthorizationRequirement requirement in requirements)
        {
            if (requirement is null)
            {
                throw new ArgumentException("Authorization requirements must not be null.", nameof(requirements));
            }
        }

        _requirements.AddRange(requirements);
        return this;
    }

    /// <summary>
    /// Requires an authenticated user: a principal with at least one authenticated identity.
    /// </summary>
    /// <returns>The same builder, for chaining.</returns>
    public AuthorizationPolicyBuilder RequireAuthenticatedUser()
    {
        _requirements.Add(DenyAnonymousRequirement.Instance);
        return this;
    }

    /// <summary>
    /// Requires the principal to be in at least one of <paramref name="roles"/>, as decided by
    /// <see cref="System.Security.Claims.ClaimsPrincipal.IsInRole(string)"/>.
    /// </summary>
    /// <param name="roles">The accepted roles. At least one.</param>
    /// <returns>The same builder, for chaining.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="roles"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="roles"/> is empty, or contains a <see langword="null"/>, empty, or whitespace entry.
    /// </exception>
    public AuthorizationPolicyBuilder RequireRole(params string[] roles)
        => RequireRole((IEnumerable<string>)roles);

    /// <summary>
    /// Requires the principal to be in at least one of <paramref name="roles"/>, as decided by
    /// <see cref="System.Security.Claims.ClaimsPrincipal.IsInRole(string)"/>.
    /// </summary>
    /// <param name="roles">The accepted roles. At least one.</param>
    /// <returns>The same builder, for chaining.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="roles"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="roles"/> is empty, or contains a <see langword="null"/>, empty, or whitespace entry.
    /// </exception>
    public AuthorizationPolicyBuilder RequireRole(IEnumerable<string> roles)
    {
        _requirements.Add(new RolesRequirement(roles));
        return this;
    }

    /// <summary>
    /// Requires the principal to carry a claim of <paramref name="claimType"/>, with any value.
    /// </summary>
    /// <param name="claimType">The claim type, compared ordinal-ignore-case.</param>
    /// <returns>The same builder, for chaining.</returns>
    /// <exception cref="ArgumentException"><paramref name="claimType"/> is <see langword="null"/>, empty, or whitespace.</exception>
    public AuthorizationPolicyBuilder RequireClaim(string claimType)
    {
        _requirements.Add(new ClaimsRequirement(claimType, allowedValues: null));
        return this;
    }

    /// <summary>
    /// Requires the principal to carry a claim of <paramref name="claimType"/> whose value is one of
    /// <paramref name="allowedValues"/>.
    /// </summary>
    /// <param name="claimType">The claim type, compared ordinal-ignore-case.</param>
    /// <param name="allowedValues">
    /// The accepted values, compared ordinal. At least one: an empty list never means "any value"
    /// (use <see cref="RequireClaim(string)"/> for that).
    /// </param>
    /// <returns>The same builder, for chaining.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="allowedValues"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="claimType"/> is <see langword="null"/>, empty, or whitespace; or
    /// <paramref name="allowedValues"/> is empty or contains a <see langword="null"/> entry.
    /// </exception>
    public AuthorizationPolicyBuilder RequireClaim(string claimType, params string[] allowedValues)
        => RequireClaim(claimType, (IEnumerable<string>)allowedValues);

    /// <summary>
    /// Requires the principal to carry a claim of <paramref name="claimType"/> whose value is one of
    /// <paramref name="allowedValues"/>.
    /// </summary>
    /// <param name="claimType">The claim type, compared ordinal-ignore-case.</param>
    /// <param name="allowedValues">
    /// The accepted values, compared ordinal. At least one: an empty list never means "any value"
    /// (use <see cref="RequireClaim(string)"/> for that).
    /// </param>
    /// <returns>The same builder, for chaining.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="allowedValues"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="claimType"/> is <see langword="null"/>, empty, or whitespace; or
    /// <paramref name="allowedValues"/> is empty or contains a <see langword="null"/> entry.
    /// </exception>
    public AuthorizationPolicyBuilder RequireClaim(string claimType, IEnumerable<string> allowedValues)
    {
        ArgumentNullException.ThrowIfNull(allowedValues);

        _requirements.Add(new ClaimsRequirement(claimType, allowedValues));
        return this;
    }

    /// <summary>
    /// Requires a synchronous assertion over the evaluation context to return <see langword="true"/>.
    /// </summary>
    /// <param name="assertion">
    /// The assertion. It is captured now and invoked for every request the policy governs, so it must be
    /// thread-safe.
    /// </param>
    /// <returns>The same builder, for chaining.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="assertion"/> is <see langword="null"/>.</exception>
    public AuthorizationPolicyBuilder RequireAssertion(Func<AuthorizationContext, bool> assertion)
    {
        _requirements.Add(new AssertionRequirement(assertion));
        return this;
    }

    /// <summary>
    /// Requires an asynchronous assertion over the evaluation context to return <see langword="true"/>.
    /// </summary>
    /// <param name="assertion">
    /// The assertion. It receives the request's cancellation token, is captured now, and is invoked for
    /// every request the policy governs, so it must be thread-safe.
    /// </param>
    /// <returns>The same builder, for chaining.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="assertion"/> is <see langword="null"/>.</exception>
    public AuthorizationPolicyBuilder RequireAssertion(Func<AuthorizationContext, CancellationToken, ValueTask<bool>> assertion)
    {
        _requirements.Add(new AssertionRequirement(assertion));
        return this;
    }

    /// <summary>
    /// Adds every requirement and authentication scheme of <paramref name="policy"/>, so a request must
    /// satisfy that policy as well as the requirements already added.
    /// </summary>
    /// <param name="policy">The policy to combine.</param>
    /// <returns>The same builder, for chaining.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="policy"/> is <see langword="null"/>.</exception>
    public AuthorizationPolicyBuilder Combine(AuthorizationPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);

        _requirements.AddRange(policy.Requirements);
        _authenticationSchemes.AddRange(policy.AuthenticationSchemes);
        return this;
    }

    /// <summary>
    /// Builds an immutable policy from the requirements and schemes added so far. Duplicate scheme names
    /// are removed.
    /// </summary>
    /// <returns>The policy.</returns>
    /// <exception cref="InvalidOperationException">
    /// No requirement has been added; a policy with none would authorize every request.
    /// </exception>
    public AuthorizationPolicy Build()
    {
        if (_requirements.Count == 0)
        {
            throw new InvalidOperationException(
                "An authorization policy needs at least one requirement; a policy with none would authorize " +
                "every request. Add one, for example RequireAuthenticatedUser().");
        }

        return new AuthorizationPolicy(_requirements, _authenticationSchemes);
    }
}
