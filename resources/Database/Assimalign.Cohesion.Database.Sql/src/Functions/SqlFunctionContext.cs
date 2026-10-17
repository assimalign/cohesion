using System.Threading;

using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Sql;

/// <summary>
/// What the engine tells a function about the call it is running in: the database, the collation
/// the call's text arguments compare under, and the statement's cancellation token.
/// </summary>
/// <remarks>
/// A ref struct, so a function cannot keep it beyond the call: a scalar function reads it from
/// <see cref="SqlArguments.Context"/>, and an aggregate receives it when the engine creates the
/// accumulator for a group (<see cref="SqlAggregateFunction.CreateAccumulator"/>). The
/// <see langword="default"/> context, which a test calling a function directly can pass, has no
/// database, the binary collation and a token that is never canceled.
/// </remarks>
public readonly ref struct SqlFunctionContext
{
    private readonly Collation? _collation;

    /// <summary>
    /// Initializes the context of one call: the engine's, or a test's that calls a function directly
    /// through <see cref="SqlArguments"/> or <see cref="SqlAggregateFunction.CreateAccumulator"/>.
    /// </summary>
    /// <param name="database">The database whose statement makes the call.</param>
    /// <param name="collation">The collation the call's input compares under; the binary collation when null.</param>
    /// <param name="cancellationToken">The statement's cancellation token.</param>
    public SqlFunctionContext(DatabaseName database, Collation? collation, CancellationToken cancellationToken)
    {
        Database = database;
        _collation = collation;
        CancellationToken = cancellationToken;
    }

    /// <summary>Gets the database whose statement makes the call; empty when a function is called directly.</summary>
    public DatabaseName Database { get; }

    /// <summary>
    /// Gets the collation the call's input compares under, resolved when the statement was
    /// planned: an explicit <c>COLLATE</c> on an argument, else an argument column's collation, else
    /// the database default. <c>MIN</c> and <c>MAX</c> compare text with it.
    /// </summary>
    public Collation Collation => _collation ?? Collation.Binary;

    /// <summary>
    /// Gets the statement's cancellation token. A function that runs long can observe it; the
    /// engine also checks it between rows.
    /// </summary>
    public CancellationToken CancellationToken { get; }
}
