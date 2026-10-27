using System;

using Assimalign.Cohesion.Database.Language;

namespace Assimalign.Cohesion.Database.Documents.Language;

/// <summary>A BEGIN, START, COMMIT or ROLLBACK statement executed by the owning session.</summary>
[global::System.Diagnostics.DebuggerDisplay("OqlTransactionExpression {Command}")]
[global::System.Diagnostics.DebuggerTypeProxy(typeof(OqlTransactionExpression.DebugView))]
public sealed class OqlTransactionExpression : OqlExpression
{
    /// <summary>Initializes a transaction control statement.</summary>
    /// <param name="command">The transaction operation.</param>
    /// <param name="isolation">The isolation level for BEGIN; ignored for COMMIT and ROLLBACK.</param>
    /// <param name="location">The source span, with an exclusive end offset.</param>
    /// <exception cref="ArgumentOutOfRangeException">The command or isolation value is undefined.</exception>
    public OqlTransactionExpression(OqlTransactionCommand command,
        OqlTransactionIsolation isolation = OqlTransactionIsolation.Snapshot, Location? location = null) : base(location)
    {
        if (!Enum.IsDefined(command)) { throw new ArgumentOutOfRangeException(nameof(command)); }
        if (!Enum.IsDefined(isolation)) { throw new ArgumentOutOfRangeException(nameof(isolation)); }
        Command = command;
        Isolation = isolation;
    }

    /// <summary>Gets the transaction operation.</summary>
    public OqlTransactionCommand Command { get; }

    /// <summary>Gets the isolation level for BEGIN; ignored for COMMIT and ROLLBACK.</summary>
    public OqlTransactionIsolation Isolation { get; }

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
