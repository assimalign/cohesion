namespace Assimalign.Cohesion.Database.Storage;

/// <summary>A coded refusal or integrity failure in disposable query storage.</summary>
public sealed class StorageTempException : StorageException
{
    /// <summary>The engine physical-byte quota was exceeded.</summary>
    public const string EngineQuotaCode = "CK05001";
    /// <summary>The statement physical-byte quota was exceeded.</summary>
    public const string StatementQuotaCode = "CK05002";
    /// <summary>The next page would cross the free-space floor.</summary>
    public const string FreeSpaceCode = "CK05003";
    /// <summary>A temporary page or record failed validation.</summary>
    public const string DamageCode = "CK05004";
    internal StorageTempException(string code, string message) : base(message) { Code = code; }
    /// <summary>Gets the stable diagnostic identity, separate from the message.</summary>
    public string Code { get; }
}
