using Assimalign.Cohesion.Database.Types;
using Assimalign.Cohesion.Database.Storage;
using Assimalign.Cohesion.Database.Indexing;

namespace Assimalign.Cohesion.Database.Sql.Internal;

/// <summary>
/// Refuses to open a database whose data-storage format this engine cannot open:
/// a format marker other than the engine's own, or no catalog storage to carry
/// one (#1099; the engine has no upgrade path, upgrades are #1152). The message
/// is engine-authored and actionable — the database, the format found, the
/// format supported and the remedy — so the wire-protocol server forwards it to a
/// client whose startup names the database, where any other open failure stays
/// an opaque internal error.
/// </summary>
internal sealed class SqlDataStorageFormatException : DatabaseException
{
    /// <summary>
    /// Initializes a new instance of the <see cref="SqlDataStorageFormatException"/> class.
    /// </summary>
    /// <param name="message">The refusal: the database, the format found, the format supported and the remedy.</param>
    public SqlDataStorageFormatException(string message)
        : base(message, [new Diagnostic { Code = "COHSQLE041", Message = message, Severity = DiagnosticSeverity.Error }])
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="SqlDataStorageFormatException"/> class
    /// for a refusal another component raised: an index tree whose B-tree page format
    /// this engine does not read.
    /// </summary>
    /// <param name="message">The refusal: the database, the format found, the format supported and the remedy.</param>
    /// <param name="inner">The component's refusal.</param>
    public SqlDataStorageFormatException(string message, System.Exception inner)
        : base(message, [new Diagnostic
        { Code = inner is StorageFormatException ? StorageFormatException.ErrorCode :
            inner is IndexFormatException ? IndexFormatException.ErrorCode : "COHSQLE041",
            Message = message, Severity = DiagnosticSeverity.Error }], inner)
    {
    }
    internal SqlDataStorageFormatException(string message, string code)
        : base(message, [new Diagnostic { Code = code, Message = message, Severity = DiagnosticSeverity.Error }]) { }
}
