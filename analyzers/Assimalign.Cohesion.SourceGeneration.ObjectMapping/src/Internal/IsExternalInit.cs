// Deviates from the repo Internal-namespace rule per design decision: the compiler binds init accessors
// to System.Runtime.CompilerServices.IsExternalInit by exact name, so the polyfill keeps that namespace.
namespace System.Runtime.CompilerServices;

/// <summary>
/// Polyfill enabling <c>init</c>-only setters (and positional records) when targeting
/// netstandard2.0, which does not define this type.
/// </summary>
internal static class IsExternalInit
{
}
