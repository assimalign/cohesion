using System;
using Assimalign.Cohesion.Database.Language;

namespace Assimalign.Cohesion.Database.Graph.Internal;

/// <summary>
/// The failure for a statement whose parse, plan or evaluation needs more stack than the executing
/// thread has left: <c>COHDBG008</c>, statement too complex (ISO SQLSTATE 54001; Neo4j reports the
/// same condition as the transient <c>Neo.TransientError.General.StackOverFlowError</c>, GQLSTATUS
/// 51N37). GQL has no fixed limit on label-expression or predicate length or nesting, as Neo4j has
/// none; the parser and every recursive walk check the stack before they descend, so the statement
/// fails instead of the process. The statement's operation aborts as for any other statement
/// failure, and the session stays open.
/// </summary>
internal static class GraphStatementTooComplex
{
    /// <summary>The engine code.</summary>
    internal const string Code = "COHDBG008";

    /// <summary>
    /// The parser's code for text whose nesting the parsing thread's stack cannot hold. The same
    /// text parses on a thread with more stack, so it is reported as <see cref="Code"/>, not as a
    /// parse error.
    /// </summary>
    internal const string ParserCode = "GQL0009";

    /// <summary>Creates the failure for a walk that ran out of stack.</summary>
    /// <param name="innerException">The exhausted-stack signal.</param>
    /// <returns>The coded failure.</returns>
    internal static DatabaseException Create(InsufficientExecutionStackException innerException)
        => new($"{Code}: Statement too complex: running it needs more stack than the executing thread has left. " +
            "Reduce the nesting of its parentheses and negations, or run it on a thread with a larger stack.", innerException);

    /// <summary>Creates the failure for a parse that ran out of stack.</summary>
    /// <param name="diagnostic">The parser's <see cref="ParserCode"/> diagnostic.</param>
    /// <returns>The coded failure.</returns>
    internal static DatabaseException FromParse(Diagnostic diagnostic)
        => Create(new InsufficientExecutionStackException($"GQL parse error {diagnostic.Code}: {diagnostic.Message}"));
}
