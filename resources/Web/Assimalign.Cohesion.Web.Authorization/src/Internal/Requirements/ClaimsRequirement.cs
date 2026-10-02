using System;
using System.Collections.Generic;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Web.Authorization.Internal;

/// <summary>
/// Requires the principal to carry a claim of a given type, optionally with one of a set of allowed
/// values. The claim type compares ordinal-ignore-case and values compare ordinal, matching
/// <see cref="ClaimsPrincipal.HasClaim(string, string)"/>. This is the requirement behind
/// <see cref="AuthorizationPolicyBuilder.RequireClaim(string)"/> and its overloads.
/// </summary>
internal sealed class ClaimsRequirement : IAuthorizationRequirement
{
    private readonly string _claimType;
    private readonly string[]? _allowedValues;

    /// <summary>
    /// Creates the requirement.
    /// </summary>
    /// <param name="claimType">The required claim type.</param>
    /// <param name="allowedValues">
    /// The accepted values, or <see langword="null"/> to accept a claim of the type with any value. When
    /// supplied, at least one value is required.
    /// </param>
    /// <exception cref="ArgumentException">
    /// <paramref name="claimType"/> is <see langword="null"/>, empty, or whitespace; or
    /// <paramref name="allowedValues"/> is empty or contains a <see langword="null"/> entry.
    /// </exception>
    public ClaimsRequirement(string claimType, IEnumerable<string>? allowedValues)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(claimType);

        _claimType = claimType;

        if (allowedValues is not null)
        {
            _allowedValues = AuthorizationNames.Copy(allowedValues, nameof(allowedValues), "allowed claim value", allowBlank: true);

            // An empty list must not quietly mean "any value": a caller who computed the list and got
            // nothing back would otherwise authorize every holder of the claim type.
            if (_allowedValues.Length == 0)
            {
                throw new ArgumentException(
                    "At least one allowed value is required. To accept a claim of the type with any value, " +
                    "use RequireClaim(claimType).",
                    nameof(allowedValues));
            }
        }
    }

    /// <summary>
    /// Gets the required claim type.
    /// </summary>
    public string ClaimType => _claimType;

    /// <summary>
    /// Gets the accepted values, or <see langword="null"/> when any value is accepted.
    /// </summary>
    public IReadOnlyList<string>? AllowedValues => _allowedValues;

    /// <inheritdoc />
    public ValueTask<bool> EvaluateAsync(AuthorizationContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        foreach (Claim claim in context.User.Claims)
        {
            if (string.Equals(claim.Type, _claimType, StringComparison.OrdinalIgnoreCase) && IsAllowed(claim.Value))
            {
                return new ValueTask<bool>(true);
            }
        }

        return new ValueTask<bool>(false);
    }

    private bool IsAllowed(string value)
    {
        if (_allowedValues is null)
        {
            return true;
        }

        foreach (string allowed in _allowedValues)
        {
            if (string.Equals(value, allowed, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }
}
