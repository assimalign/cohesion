namespace Assimalign.Cohesion.Database.Documents.Language.Internal;

/// <summary>One entry of <see cref="OqlUnsupportedVocabulary"/>.</summary>
/// <param name="Spelling">The upper-case word.</param>
/// <param name="Construct">The construct the diagnostic names.</param>
/// <param name="Position">Where the capability scan treats the word as that construct.</param>
internal readonly record struct OqlUnsupportedWord(string Spelling, string Construct, OqlWordPosition Position);
