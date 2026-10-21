using System;
using System.Collections.Generic;
using System.Globalization;

using Assimalign.Cohesion.Database.Indexing;
using Assimalign.Cohesion.Database.Storage;
using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database;

/// <summary>
/// Reports detected storage or attached-index corruption at an engine boundary, naming the database and,
/// when available, the affected page. The operation failed; corruption is never absence.
/// </summary>
public sealed class DatabaseCorruptionException : DatabaseException
{
    private DatabaseCorruptionException(DatabaseName database, Exception cause, PageId? pageId, string message, IEnumerable<Diagnostic>? additionalDiagnostics)
        : base(message, [new Diagnostic { Code = StorageCorruptionException.ErrorCode, Message = message, Severity = DiagnosticSeverity.Error },
            .. additionalDiagnostics ?? []], cause)
    {
        Database = database;
        PageId = pageId;
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
    /// <param name="additionalDiagnostics">Additional engine diagnostics, including its model code when open is refused.</param>
    /// <returns>The coded database failure.</returns>
    /// <exception cref="ArgumentException"><paramref name="database"/> is empty.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="cause"/> is null.</exception>
    public static DatabaseCorruptionException Create(DatabaseName database, StorageCorruptionException cause, IEnumerable<Diagnostic>? additionalDiagnostics = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(database);
        ArgumentNullException.ThrowIfNull(cause);
        string message = string.Format(CultureInfo.CurrentCulture,
            cause.PageId is null ? ErrorMessages.StorageCorrupt : ErrorMessages.StoragePageCorrupt,
            database, cause.PageId, cause.Message);
        return new DatabaseCorruptionException(database, cause, cause.PageId, message, additionalDiagnostics);
    }

    /// <summary>Creates the engine's report of damage in an attached index page.</summary>
    /// <param name="database">The affected database.</param>
    /// <param name="cause">The index integrity violation, retained as the inner exception.</param>
    /// <param name="additionalDiagnostics">Additional model diagnostics.</param>
    /// <returns>The common corruption signal, including the index corruption diagnostic.</returns>
    /// <exception cref="ArgumentException"><paramref name="database"/> is empty.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="cause"/> is null.</exception>
    public static DatabaseCorruptionException Create(DatabaseName database, IndexCorruptionException cause, IEnumerable<Diagnostic>? additionalDiagnostics = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(database);
        ArgumentNullException.ThrowIfNull(cause);
        string message = string.Format(CultureInfo.CurrentCulture, ErrorMessages.StoragePageCorrupt,
            database, cause.PageId, cause.Message.StartsWith(IndexCorruptionException.ErrorCode + ": ", StringComparison.Ordinal)
                ? cause.Message[(IndexCorruptionException.ErrorCode.Length + 2)..] : cause.Message);
        return new DatabaseCorruptionException(database, cause, (PageId)cause.PageId, message,
            [new Diagnostic { Code = IndexCorruptionException.ErrorCode, Message = cause.Message, Severity = DiagnosticSeverity.Error },
                .. additionalDiagnostics ?? []]);
    }
}
