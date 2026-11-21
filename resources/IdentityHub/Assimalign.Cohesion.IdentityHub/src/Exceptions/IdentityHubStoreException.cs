using System;
using System.Globalization;

namespace Assimalign.Cohesion.IdentityHub;

/// <summary>
/// The exception an <see cref="IIdentityHubStore"/>, or the hub reading through one, raises when
/// a store operation fails.
/// </summary>
/// <remarks>
/// <para>
/// A store implementation translates the failures of its own medium into this type, with the
/// medium's exception as the inner exception, so the hub handles one exception type whatever
/// store it runs on. The message names the record set and key at most, never a payload.
/// </para>
/// <para>
/// A refused conditional write is not a failure and is never reported with this exception; it
/// is an <see cref="IdentityHubWriteResult"/>.
/// </para>
/// </remarks>
public sealed class IdentityHubStoreException : IdentityHubException
{
    // The message of a refused continuation is read once: a client that replays a stale token
    // can raise this exception on a normal path.
    private static readonly string _continuationInvalid = ErrorMessages.ContinuationInvalid;

    /// <summary>
    /// Initializes the exception with its category and message.
    /// </summary>
    /// <param name="code">The failure's category.</param>
    /// <param name="message">The message that describes the failure.</param>
    public IdentityHubStoreException(IdentityHubErrorCode code, string message)
        : base(code, message)
    {
    }

    /// <summary>
    /// Initializes the exception with its category, message, and cause.
    /// </summary>
    /// <param name="code">The failure's category.</param>
    /// <param name="message">The message that describes the failure.</param>
    /// <param name="innerException">The exception of the store's medium that caused the failure.</param>
    public IdentityHubStoreException(IdentityHubErrorCode code, string message, Exception? innerException)
        : base(code, message, innerException)
    {
    }

    /// <summary>
    /// Gets the record set the failed operation addressed, when it is known.
    /// </summary>
    public IdentityHubRecordSetName? Set { get; init; }

    /// <summary>
    /// Gets the key of the record the failed operation addressed, when it is known.
    /// </summary>
    public IdentityHubRecordKey? Key { get; init; }

    /// <summary>
    /// Creates the exception for a continuation token the store did not issue for the query it
    /// was passed with.
    /// </summary>
    /// <param name="set">The record set the query addressed.</param>
    /// <param name="innerException">The exception raised while reading the token, when there is one.</param>
    /// <returns>
    /// An exception with <see cref="IdentityHubErrorCode.StoreContinuationInvalid"/>. The token is
    /// not part of the message.
    /// </returns>
    public static IdentityHubStoreException ContinuationInvalid(
        IdentityHubRecordSetName set,
        Exception? innerException = null)
    {
        return new IdentityHubStoreException(
            IdentityHubErrorCode.StoreContinuationInvalid,
            _continuationInvalid,
            innerException)
        {
            Set = set.Value is null ? default(IdentityHubRecordSetName?) : set,
        };
    }

    /// <summary>
    /// Creates the exception for a stored record that could not be read back.
    /// </summary>
    /// <param name="set">The record set the record is in.</param>
    /// <param name="key">The record's key.</param>
    /// <param name="innerException">The exception raised while decoding the record, when there is one.</param>
    /// <returns>
    /// An exception with <see cref="IdentityHubErrorCode.StoreRecordCorrupt"/>. The payload is not
    /// part of the message.
    /// </returns>
    public static IdentityHubStoreException RecordCorrupt(
        IdentityHubRecordSetName set,
        IdentityHubRecordKey key,
        Exception? innerException = null)
    {
        return new IdentityHubStoreException(
            IdentityHubErrorCode.StoreRecordCorrupt,
            string.Format(CultureInfo.CurrentCulture, ErrorMessages.StoreRecordCorrupt, set, key),
            innerException)
        {
            Set = set.Value is null ? default(IdentityHubRecordSetName?) : set,
            Key = key.Value is null ? default(IdentityHubRecordKey?) : key,
        };
    }
}
