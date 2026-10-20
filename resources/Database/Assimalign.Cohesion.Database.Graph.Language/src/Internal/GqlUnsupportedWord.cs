namespace Assimalign.Cohesion.Database.Graph.Language.Internal;

/// <summary>One entry of <see cref="GqlUnsupportedVocabulary"/>.</summary>
/// <param name="Spelling">The upper-case word or the operator.</param>
/// <param name="Construct">The construct the diagnostic names.</param>
/// <param name="Position">Where the capability scan treats the spelling as that construct.</param>
internal readonly record struct GqlUnsupportedWord(string Spelling, string Construct, GqlWordPosition Position);
