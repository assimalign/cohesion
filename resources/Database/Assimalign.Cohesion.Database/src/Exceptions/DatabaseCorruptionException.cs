using System;
using System.Globalization;

using Assimalign.Cohesion.Database.Storage;
using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database;

/// <summary>
/// Reports detected storage corruption at an engine boundary, naming the database and,
/// when available, the affected page. The operation failed; corruption is never absence.
/// </summary>
public sealed class DatabaseCorruptionException : DatabaseException
{
    private DatabaseCorruptionException(DatabaseName database, StorageCorruptionException cause, string message)
        : base(message, [new Diagnostic { Code = StorageCorruptionException.ErrorCode, Message = message, Severity = DiagnosticSeverity.Error }], cause)
    {
        Database = database;
        PageId = cause.PageId;
    }

    /// <summary>Gets the stable storage corruption diagnostic code.</summary>
    public string Code => StorageCorruptionException.ErrorCode;

    /// <summary>Gets the affected database.</summary>
    public DatabaseName Database { get; }

    /// <summary>Gets the affected page, or null for corruption without a page location.</summary>
    public PageId? PageId { get; }

    /// <summary>Creates the engine's actionable report of a storage integrity violation.</summary>
    /// <param name="database">The affected database.</param>
    /// <param name="cause">The storage integrity violation, retained as the inner exception.</param>
    /// <returns>The coded database failure.</returns>
    /// <exception cref="ArgumentException"><paramref name="database"/> is empty.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="cause"/> is null.</exception>
    public static DatabaseCorruptionException Create(DatabaseName database, StorageCorruptionException cause)
    {
        ArgumentException.ThrowIfNullOrEmpty(database);
        ArgumentNullException.ThrowIfNull(cause);
        string message = string.Format(CultureInfo.CurrentCulture,
            cause.PageId is null ? ErrorMessages.StorageCorrupt : ErrorMessages.StoragePageCorrupt,
            database, cause.PageId, cause.Message);
        return new DatabaseCorruptionException(database, cause, message);
    }
}
