using System.Text.RegularExpressions;

namespace Assimalign.Cohesion.Web.AotGuard;

/// <summary>
/// The guard's source-generated rewrite pattern: <c>[GeneratedRegex]</c> is how an application gets
/// compiled-speed matching under NativeAOT, where <see cref="RegexOptions.Compiled"/> falls back to the
/// interpreter.
/// </summary>
internal static partial class GuardRewriteRules
{
    [GeneratedRegex("^/catalog/(?<id>\\d+)$")]
    public static partial Regex CatalogValue();
}
