namespace Assimalign.Cohesion.Database.Documents.Language.Internal;

/// <summary>Where the OQL capability scan treats a recognized-unsupported word as its construct.</summary>
internal enum OqlWordPosition
{
    /// <summary>Everywhere except a path segment after a dot. All are lexer keywords or functions.</summary>
    Anywhere,

    /// <summary>
    /// As the first word of the statement, or directly after the end of an operand (a name,
    /// literal, parameter, closing bracket, or <c>ASC</c>/<c>DESC</c>). Elsewhere it is a name.
    /// So a <see cref="Clause"/> word cannot be an AS-less <c>FROM</c> alias; <c>AS</c> keeps it
    /// a name.
    /// </summary>
    Clause,

    /// <summary>
    /// After a parsed statement, so destination, binding and other identifier positions
    /// are consumed as names first. The capability scan still rejects a statement prefix.
    /// </summary>
    Trailing,

    /// <summary>
    /// Only as the first word of the statement, where it is a statement verb
    /// (<c>MERGE INTO c ...</c>). Enabled verbs bypass the capability refusal. Elsewhere, including an AS-less <c>FROM</c> alias, it
    /// is a name.
    /// </summary>
    Statement,

    /// <summary>Only before a function call's opening parenthesis; otherwise an identifier.</summary>
    Function,

    /// <summary>
    /// Only as part of a quantified predicate, <c>EVERY x IN ... SATISFIES ... END</c>, which the
    /// scan recognizes by its <c>name IN</c> binding and reports once at the quantifier.
    /// <c>ANY</c> and <c>SOME</c> open the same predicate. Elsewhere it is a name.
    /// </summary>
    Quantifier,
}
