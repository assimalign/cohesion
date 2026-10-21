using System;
using System.Threading;

using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Sql;

/// <summary>
/// What the engine tells a function about the call it is running in: the database, the collation
/// the call's text arguments compare under, the transaction timestamp, and the statement's cancellation token.
/// </summary>
/// <remarks>
/// A ref struct, so a function cannot keep it beyond the call: a scalar function reads it from
/// <see cref="SqlArguments.Context"/>, and an aggregate receives it when the engine creates the
/// accumulator for a group (<see cref="SqlAggregateFunction.CreateAccumulator"/>). The
/// <see langword="default"/> context, which a test calling a function directly can pass, has no
/// database, the binary collation, a token that is never canceled, and a transaction timestamp of
/// <see cref="DateTimeOffset.MinValue"/>.
/// </remarks>
public readonly ref struct SqlFunctionContext
{
    private readonly Collation? _collation;
    private readonly DateTimeOffset? _transactionStart;

    internal DateTimeOffset? TransactionStart => _transactionStart;

    internal SqlFunctionContext(DatabaseName database, Collation? collation, CancellationToken cancellationToken, DateTimeOffset? transactionStart)
    {
        _transactionStart = transactionStart;
        Database = database;
        _collation = collation;
        CancellationToken = cancellationToken;
    }

    /// <summary>
    /// Initializes the context of one call: the engine's, or a test's that calls a function directly
    /// through <see cref="SqlArguments"/> or <see cref="SqlAggregateFunction.CreateAccumulator"/>.
    /// </summary>
    /// <param name="database">The database whose statement makes the call.</param>
    /// <param name="collation">The collation the call's input compares under; the binary collation when null.</param>
    /// <param name="cancellationToken">The statement's cancellation token.</param>
    /// <remarks>Captures the current UTC time as the transaction timestamp for a direct call.</remarks>
    public SqlFunctionContext(DatabaseName database, Collation? collation, CancellationToken cancellationToken)
        : this(database, collation, cancellationToken, DateTimeOffset.UtcNow)
    {
    }

    /// <summary>Initializes a call context with an explicit transaction timestamp.</summary>
    /// <param name="database">The database whose statement makes the call.</param>
    /// <param name="collation">The input collation; the binary collation when null.</param>
    /// <param name="cancellationToken">The statement's cancellation token.</param>
    /// <param name="transactionTimestamp">The transaction's start time.</param>
    public SqlFunctionContext(DatabaseName database, Collation? collation, CancellationToken cancellationToken,
        DateTimeOffset transactionTimestamp)
        : this(database, collation, cancellationToken, (DateTimeOffset?)transactionTimestamp)
    {
    }

    /// <summary>
    /// Gets the transaction's start time, shared by every statement in an explicit transaction.
    /// An autocommit statement receives its own start time.
    /// </summary>
    /// <remarks>The default context returns <see cref="DateTimeOffset.MinValue"/>.</remarks>
    public DateTimeOffset TransactionTimestamp => _transactionStart.GetValueOrDefault();

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
