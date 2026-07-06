namespace System.Runtime.CompilerServices;

/// <summary>
/// Polyfill enabling <c>init</c>-only setters (and positional records) when targeting
/// netstandard2.0, which does not define this type.
/// </summary>
internal static class IsExternalInit
{
}
