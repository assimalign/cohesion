using System.Globalization;
using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Documents.Catalog;

/// <summary>A document index constraint refused a write or an index build.</summary>
public sealed class DocumentConstraintViolationException : DatabaseException
{
    private DocumentConstraintViolationException(string indexName, string? collectionName, string? path, string code, string message)
        : base(message, [new Diagnostic { Code = code, Message = message, Severity = DiagnosticSeverity.Error }])
    { IndexName = indexName; CollectionName = collectionName; Path = path; }

    /// <summary>Gets the index that refused the operation.</summary>
    public string IndexName { get; }
    /// <summary>Gets the collection, when the refusal concerns a duplicate key.</summary>
    public string? CollectionName { get; }
    /// <summary>Gets the path containing a non-scalar UNIQUE component, when applicable.</summary>
    public string? Path { get; }
    /// <summary>Gets PostgreSQL's unique-violation SQLSTATE, or null for an invalid component.</summary>
    public string? SqlState => Path is null ? "23505" : null;

    internal static DocumentConstraintViolationException Duplicate(string index, string collection)
        => new(index, collection, null, "COHDOC020", string.Format(CultureInfo.CurrentCulture, ErrorMessages.UniqueIndexViolation, index, collection));

    internal static DocumentConstraintViolationException NonScalar(string index, string path)
        => new(index, null, path, "COHDOC021", string.Format(CultureInfo.CurrentCulture, ErrorMessages.UniqueIndexNonScalar, index, path));
}
