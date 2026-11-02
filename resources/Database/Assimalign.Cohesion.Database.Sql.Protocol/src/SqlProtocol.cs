using Assimalign.Cohesion.Database.Protocol;

namespace Assimalign.Cohesion.Database.Sql.Protocol;

/// <summary>The immutable Sql endpoint message family.</summary>
public static partial class SqlProtocol
{
    /// <summary>Gets the independently negotiated bounded SQL table export capability.</summary>
    public static ProtocolCapability OutboundStream { get; } = new("sql.outbound-stream", 80, 81, 82, 83, 84);

    /// <summary>Gets the family bound to every Sql protocol connection.</summary>
    public static ProtocolMessageFamily Family { get; } = new("Sql", [ProtocolCapability.Diagnostics, ProtocolCapability.TransactionStatus, OutboundStream], "DISCARD ALL", 5, 6, 7, 8, 9, 80, 81, 82, 83, 84);

    extension(ProtocolMessageType)
    {
        /// <summary>Executes a model statement. Source-compatible model-owned identifier.</summary>
        public static ProtocolMessageType Execute => (ProtocolMessageType)SqlProtocolMessageType.Execute;
        /// <summary>Describes result columns. Source-compatible model-owned identifier.</summary>
        public static ProtocolMessageType ResultHeader => (ProtocolMessageType)SqlProtocolMessageType.ResultHeader;
        /// <summary>Carries one encoded result row. Source-compatible model-owned identifier.</summary>
        public static ProtocolMessageType ResultRow => (ProtocolMessageType)SqlProtocolMessageType.ResultRow;
        /// <summary>Completes a statement. Source-compatible model-owned identifier.</summary>
        public static ProtocolMessageType ResultComplete => (ProtocolMessageType)SqlProtocolMessageType.ResultComplete;
        /// <summary>Reserved transaction control identifier. Source-compatible model-owned identifier.</summary>
        public static ProtocolMessageType Transaction => (ProtocolMessageType)SqlProtocolMessageType.Transaction;
    }
}
