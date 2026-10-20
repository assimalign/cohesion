namespace Assimalign.Cohesion.Database.Sql.Language.Internal;

/// <summary>One entry of <see cref="SqlUnsupportedVocabulary"/>.</summary>
/// <param name="Spelling">The upper-case word or the operator.</param>
/// <param name="Construct">
/// The construct the diagnostic names. A word that starts more than one construct names the
/// text they share: <c>GROUPING</c>
/// for <c>GROUPING SETS</c> and the <c>GROUPING(...)</c> call.
/// </param>
/// <param name="Position">Where the parser treats the spelling as that construct.</param>
internal readonly record struct SqlUnsupportedWord(string Spelling, string Construct, SqlWordPosition Position);
