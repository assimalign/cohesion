namespace Assimalign.Cohesion.Database.Execution;

/// <summary>
/// The base of every request a database session executes. It carries no statement and no
/// parameter binding: a model with a query language puts its parsed statement and its parameters
/// on its own request type, and a model with none puts its typed operands there.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why it has no members.</b> A session's typed execute member takes its model's request root
/// as a type argument (<c>DatabaseExecutionSession&lt;TRequest&gt;</c> in the area root, constrained
/// to this type), so a request of another model does not compile. Nothing dispatches on a
/// <see cref="DatabaseRequest"/>: it exists for that constraint and for the root event source,
/// which reads only the request's type name.
/// </para>
/// <para>
/// <b>The variant set.</b> Each model declares its request family in its own assembly: the SQL
/// model's <c>SqlQueryRequest</c>, the document model's <c>DocumentQueryRequest</c>, the graph
/// model's <c>GraphRequest</c> and the key-value model's <c>KeyValueRequest</c>. The constructor is
/// therefore <c>protected</c>. The package references no language assembly, so the request and
/// result roots name no query language.
/// </para>
/// </remarks>
// Deviates from the repo interface-first rule per design decision: Database engines are concrete-first — abstract bases with protected cores and sealed model leaves (owner, 2026-10-04; database-area.md).
public abstract class DatabaseRequest
{
    /// <summary>
    /// Initializes a new request.
    /// </summary>
    protected DatabaseRequest()
    {
    }
}
