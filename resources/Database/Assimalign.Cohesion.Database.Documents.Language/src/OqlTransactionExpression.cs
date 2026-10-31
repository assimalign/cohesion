using System;

using Assimalign.Cohesion.Database.Language;

namespace Assimalign.Cohesion.Database.Documents.Language;

/// <summary>A transaction or named savepoint control executed by the owning session.</summary>
[global::System.Diagnostics.DebuggerDisplay("OqlTransactionExpression {Command}")]
[global::System.Diagnostics.DebuggerTypeProxy(typeof(OqlTransactionExpression.DebugView))]
public sealed class OqlTransactionExpression : OqlExpression
{
    /// <summary>Initializes a transaction control statement.</summary>
    /// <param name="command">The transaction operation.</param>
    /// <param name="isolation">The isolation level used only for BEGIN.</param>
    /// <param name="location">The source span, with an exclusive end offset.</param>
    /// <exception cref="ArgumentException">A named control has no savepoint name.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The command or isolation value is undefined.</exception>
    public OqlTransactionExpression(OqlTransactionCommand command,
        OqlTransactionIsolation isolation = OqlTransactionIsolation.Snapshot, Location? location = null)
        : this(command, isolation, location, null)
    {
    }

    // Names enter only through the parser, as in SQL/GQL. The public constructor
    // retains its existing enum/location surface; named controls need parsed names.
    internal OqlTransactionExpression(OqlTransactionCommand command,
        OqlTransactionIsolation isolation, Location? location, string? savepointName) : base(location)
    {
        if (!Enum.IsDefined(command)) { throw new ArgumentOutOfRangeException(nameof(command)); }
        if (!Enum.IsDefined(isolation)) { throw new ArgumentOutOfRangeException(nameof(isolation)); }
        if (command is OqlTransactionCommand.Savepoint or OqlTransactionCommand.ReleaseSavepoint or OqlTransactionCommand.RollbackToSavepoint)
        { ArgumentException.ThrowIfNullOrEmpty(savepointName); }
        SavepointName = savepointName;
        Command = command;
        Isolation = isolation;
    }

    /// <summary>Gets the transaction operation.</summary>
    public OqlTransactionCommand Command { get; }

    /// <summary>Gets the isolation level used only for BEGIN.</summary>
    public OqlTransactionIsolation Isolation { get; }

    /// <summary>Gets the decoded savepoint name, or null for another control.</summary>
    public string? SavepointName { get; }

    /// <summary>Renders canonical OQL transaction control text.</summary>
    /// <returns>The control with its quoted identifier, when present.</returns>
    public override string ToString()
    {
        string command = Command switch
        {
            OqlTransactionCommand.Begin => Isolation == OqlTransactionIsolation.ReadCommitted ? "BEGIN ISOLATION LEVEL READ COMMITTED" : "BEGIN",
            OqlTransactionCommand.Commit => "COMMIT",
            OqlTransactionCommand.Rollback => "ROLLBACK",
            OqlTransactionCommand.Savepoint => "SAVEPOINT",
            OqlTransactionCommand.ReleaseSavepoint => "RELEASE SAVEPOINT",
            _ => "ROLLBACK TO SAVEPOINT",
        };
        return SavepointName is { } name ? command + " \"" + name.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"" : command;
    }

    private sealed class DebugView
    {
        private readonly OqlTransactionExpression _target;

        public DebugView(OqlTransactionExpression target) => _target = target;

        public OqlTransactionCommand Command => _target.Command;
        public OqlTransactionIsolation Isolation => _target.Isolation;
        public string? Text => _target.Text;
        public Location? Location => _target.Location;
    }
}
