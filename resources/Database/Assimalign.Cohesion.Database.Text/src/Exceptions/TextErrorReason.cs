namespace Assimalign.Cohesion.Database.Text;

/// <summary>Identifies a text-analysis failure independently of model diagnostic codes.</summary>
public enum TextErrorReason
{
    /// <summary>The persisted analyzer name is unavailable.</summary>
    UnknownAnalyzer,
    /// <summary>The persisted analyzer version differs from the implementation.</summary>
    AnalyzerVersionMismatch,
    /// <summary>The input contains an invalid Unicode scalar sequence.</summary>
    InvalidUnicode,
    /// <summary>An explicit lexeme is empty, contains a null, or exceeds the byte limit.</summary>
    InvalidLexeme,
    /// <summary>The vector's lexeme and position payload exceeds the size limit.</summary>
    VectorTooLarge,
}
