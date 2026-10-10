using System;

namespace Assimalign.Cohesion.Web.SecurityHeaders;

/// <summary>
/// An immutable, validated <c>Permissions-Policy</c> field value (W3C Permissions Policy), emitted by the
/// security-headers middleware when set on <see cref="SecurityHeadersPolicy.PermissionsPolicy"/>.
/// </summary>
/// <remarks>
/// Build one with <see cref="Create"/> or a <see cref="PermissionsPolicyBuilder"/>, for example
/// <c>PermissionsPolicy.Create(policy =&gt; policy.Disable("camera").Disable("microphone").AllowSelf("geolocation"))</c>.
/// </remarks>
public sealed class PermissionsPolicy
{
    private readonly string _value;

    internal PermissionsPolicy(string value)
    {
        _value = value;
    }

    /// <summary>
    /// Builds a policy with a <see cref="PermissionsPolicyBuilder"/>.
    /// </summary>
    /// <param name="configure">The callback that sets each feature's allowlist.</param>
    /// <returns>The policy.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="configure"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">A feature name or origin is not valid.</exception>
    /// <exception cref="InvalidOperationException">The callback set no feature.</exception>
    public static PermissionsPolicy Create(Action<PermissionsPolicyBuilder> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);

        PermissionsPolicyBuilder builder = new();
        configure(builder);
        return builder.Build();
    }

    /// <summary>
    /// Returns the field value, an RFC 9651 Structured Field Dictionary such as <c>camera=(), geolocation=(self)</c>.
    /// </summary>
    /// <returns>The serialized <c>Permissions-Policy</c> field value.</returns>
    public override string ToString() => _value;
}
