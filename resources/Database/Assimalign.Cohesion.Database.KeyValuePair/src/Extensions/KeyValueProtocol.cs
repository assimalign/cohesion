using Assimalign.Cohesion.Database.Protocol;

namespace Assimalign.Cohesion.Database.KeyValuePair;

/// <summary>The immutable KeyValuePair endpoint message family.</summary>
public static class KeyValueProtocol
{
    /// <summary>Gets the family bound to every KeyValuePair protocol connection.</summary>
    public static ProtocolMessageFamily Family { get; } = new("KeyValuePair", 5, 6, 7, 8, 9);

    extension(ProtocolMessageType)
    {
        /// <summary>Executes a model statement. Source-compatible model-owned identifier.</summary>
        public static ProtocolMessageType Execute => (ProtocolMessageType)KeyValueProtocolMessageType.Execute;
        /// <summary>Describes result columns. Source-compatible model-owned identifier.</summary>
        public static ProtocolMessageType ResultHeader => (ProtocolMessageType)KeyValueProtocolMessageType.ResultHeader;
        /// <summary>Carries one encoded result row. Source-compatible model-owned identifier.</summary>
        public static ProtocolMessageType ResultRow => (ProtocolMessageType)KeyValueProtocolMessageType.ResultRow;
        /// <summary>Completes a statement. Source-compatible model-owned identifier.</summary>
        public static ProtocolMessageType ResultComplete => (ProtocolMessageType)KeyValueProtocolMessageType.ResultComplete;
        /// <summary>Reserved transaction control identifier. Source-compatible model-owned identifier.</summary>
        public static ProtocolMessageType Transaction => (ProtocolMessageType)KeyValueProtocolMessageType.Transaction;
    }
}
