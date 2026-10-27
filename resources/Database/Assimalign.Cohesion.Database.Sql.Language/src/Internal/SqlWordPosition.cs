namespace Assimalign.Cohesion.Database.Sql.Language.Internal;

/// <summary>Where the SQL parser treats a recognized-unsupported spelling as its construct.</summary>
internal enum SqlWordPosition
{
    /// <summary>Wherever the lexer reports the keyword, found by the preflight clause scan.</summary>
    Clause,

    /// <summary>
    /// Where a dedicated rule recognizes it: <c>WITH</c> as a statement prefix; <c>ALL</c> after
    /// <c>SELECT</c> or a comparison operator;
    /// <c>COLLATION</c> and <c>FULLTEXT</c> after <c>CREATE</c>; <c>GROUPING SETS</c> and the <c>GROUPING</c>, <c>ROLLUP</c>, <c>CUBE</c> and
    /// <c>GROUPING_ID</c> calls; <c>PERCENT</c> and <c>WITH TIES</c> after FETCH or TOP;
    /// <c>FILTER (...)</c> and <c>WITHIN GROUP</c> after a call;
    /// <c>ANY</c> and <c>SOME</c> with an array operand after a comparison operator; <c>LATERAL</c> after
    /// <c>FROM</c> or <c>JOIN</c>; and <c>~</c> in prefix or infix position.
    /// </summary>
    Rule,

    /// <summary>Before a function's opening parenthesis.</summary>
    Function,

    /// <summary>A window function, owned by the clause preflight.</summary>
    WindowFunction,

    /// <summary>A datetime value function, with or without parentheses.</summary>
    NiladicFunction,
}
