using System;

using Assimalign.Cohesion.Database.Storage.Internal;

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
    /// <summary>Temporary storage is unavailable, including a lease, volume or file I/O failure.</summary>
    public const string UnavailableCode = "CK05005";
    /// <summary>An encoded sort record exceeds its declared maximum.</summary>
    public const string RecordTooLargeCode = "CK05006";
    internal static StorageTempException Unavailable(Exception cause)
    {
        StorageEventSource.Log.TempRefused(UnavailableCode);
        return new StorageTempException(UnavailableCode, ErrorMessages.TempUnavailable, cause);
    }
    internal static StorageTempException Damage()
    {
        StorageEventSource.Log.TempDamaged(DamageCode);
        return new StorageTempException(DamageCode, ErrorMessages.TempPageDamaged);
    }
    private StorageTempException(string code, string message, Exception cause) : base(message, cause) { Code = code; }
    internal StorageTempException(string code, string message) : base(message) { Code = code; }
    /// <summary>Gets the stable diagnostic identity, separate from the message.</summary>
    public string Code { get; }
}
