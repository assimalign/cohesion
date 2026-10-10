namespace Assimalign.Cohesion.Database.Graph.Language.Internal;

/// <summary>
/// The label-expression spellings the GQL profile executes (#1139): the ISO/IEC 39075 operators
/// <c>|</c>, <c>&amp;</c>, <c>!</c> and <c>%</c>, and the positional word of
/// <c>IS [NOT] LABELED</c>.
/// </summary>
/// <remarks>
/// #1101 recognized the four operators as unsupported (<c>COHDBL001</c>); #1139 made them
/// executable and flipped those pins. The capability scan reads them as label syntax, never as
/// words to look up, and <c>GqlKeywordDispositionTests</c> keeps a supported case for each
/// spelling here, so one cannot regress to a capability error unnoticed.
/// </remarks>
internal static class GqlLabelVocabulary
{
    /// <summary>
    /// The positional word of ISO's <c>&lt;labeled predicate&gt;</c>. It is not a lexer keyword:
    /// only after <c>IS</c> or <c>IS NOT</c> in a predicate does it introduce a label expression,
    /// and elsewhere it is a name.
    /// </summary>
    internal const string Labeled = "LABELED";

    /// <summary>Every executable label-expression spelling, operators first.</summary>
    internal static readonly string[] Spellings = ["|", "&", "!", "%", Labeled];
}
