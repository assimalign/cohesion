namespace Assimalign.Cohesion.Web.SecurityHeaders.Internal;

/// <summary>
/// One validated directive of a <see cref="ContentSecurityPolicy"/>: either a source list (fetch,
/// <c>base-uri</c> and <c>form-action</c> directives) or a raw value that its builder method already
/// checked against the directive's own grammar.
/// </summary>
/// <param name="Name">The lowercase directive name.</param>
/// <param name="Sources">
/// The serialized source expressions of a source-list directive, in order; a <see langword="null"/>
/// element is the per-request nonce source. <see langword="null"/> for a directive with a raw value.
/// </param>
/// <param name="Value">
/// The raw value of a directive that is not a source list; the empty string for a valueless directive
/// such as <c>upgrade-insecure-requests</c>. <see langword="null"/> for a source-list directive.
/// </param>
internal readonly record struct ContentSecurityPolicyDirective(string Name, string?[]? Sources, string? Value);
