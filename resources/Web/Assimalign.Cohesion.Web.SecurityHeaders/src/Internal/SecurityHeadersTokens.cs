using System;

namespace Assimalign.Cohesion.Web.SecurityHeaders.Internal;

/// <summary>
/// Maps the public enumerations to the field tokens their specifications define. Switch expressions
/// rather than enum-name reflection keep the mapping AOT-clean, and an undefined numeric value fails
/// when the policy is compiled instead of reaching the wire.
/// </summary>
internal static class SecurityHeadersTokens
{
    public static string ToToken(ReferrerPolicy value) => value switch
    {
        ReferrerPolicy.NoReferrer => "no-referrer",
        ReferrerPolicy.NoReferrerWhenDowngrade => "no-referrer-when-downgrade",
        ReferrerPolicy.Origin => "origin",
        ReferrerPolicy.OriginWhenCrossOrigin => "origin-when-cross-origin",
        ReferrerPolicy.SameOrigin => "same-origin",
        ReferrerPolicy.StrictOrigin => "strict-origin",
        ReferrerPolicy.StrictOriginWhenCrossOrigin => "strict-origin-when-cross-origin",
        ReferrerPolicy.UnsafeUrl => "unsafe-url",
        _ => throw Undefined(nameof(ReferrerPolicy), value),
    };

    public static string ToToken(CrossOriginOpenerPolicy value) => value switch
    {
        CrossOriginOpenerPolicy.UnsafeNone => "unsafe-none",
        CrossOriginOpenerPolicy.SameOriginAllowPopups => "same-origin-allow-popups",
        CrossOriginOpenerPolicy.SameOrigin => "same-origin",
        CrossOriginOpenerPolicy.NoopenerAllowPopups => "noopener-allow-popups",
        _ => throw Undefined(nameof(CrossOriginOpenerPolicy), value),
    };

    public static string ToToken(CrossOriginEmbedderPolicy value) => value switch
    {
        CrossOriginEmbedderPolicy.UnsafeNone => "unsafe-none",
        CrossOriginEmbedderPolicy.RequireCorp => "require-corp",
        CrossOriginEmbedderPolicy.Credentialless => "credentialless",
        _ => throw Undefined(nameof(CrossOriginEmbedderPolicy), value),
    };

    public static string ToToken(CrossOriginResourcePolicy value) => value switch
    {
        CrossOriginResourcePolicy.SameSite => "same-site",
        CrossOriginResourcePolicy.SameOrigin => "same-origin",
        CrossOriginResourcePolicy.CrossOrigin => "cross-origin",
        _ => throw Undefined(nameof(CrossOriginResourcePolicy), value),
    };

    public static string ToToken(ContentSecurityPolicyHashAlgorithm value) => value switch
    {
        ContentSecurityPolicyHashAlgorithm.Sha256 => "sha256",
        ContentSecurityPolicyHashAlgorithm.Sha384 => "sha384",
        ContentSecurityPolicyHashAlgorithm.Sha512 => "sha512",
        _ => throw Undefined(nameof(ContentSecurityPolicyHashAlgorithm), value),
    };

    private static ArgumentOutOfRangeException Undefined<TEnum>(string typeName, TEnum value)
        where TEnum : struct, Enum
    {
        return new ArgumentOutOfRangeException(
            nameof(value),
            value,
            $"'{value}' is not a defined {typeName} value.");
    }
}
