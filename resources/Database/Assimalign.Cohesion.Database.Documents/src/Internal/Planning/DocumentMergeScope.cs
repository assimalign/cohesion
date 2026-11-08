namespace Assimalign.Cohesion.Database.Documents.Internal;

// INSERT retains the target's lexical identity while its value and metadata are MISSING.
internal sealed record DocumentMergeScope(string SourceName, string? TargetName, bool TargetIsMissing, bool SourceIsStored);
