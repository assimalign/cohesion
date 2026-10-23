namespace Assimalign.Cohesion.Database.Protocol;

/// <summary>The server's explicit transaction state at a completed Execute boundary.</summary>
public enum ProtocolTransactionStatus : byte
{
    /// <summary>No explicit transaction is open.</summary>
    Idle = (byte)'I',
    /// <summary>An explicit transaction is accepting statements.</summary>
    Active = (byte)'T',
    /// <summary>An explicit transaction is failed and requires rollback.</summary>
    Failed = (byte)'E'
}
