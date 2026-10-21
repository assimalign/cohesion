namespace Assimalign.Cohesion.Database.Sql.Internal;

/// <summary>
/// How a SELECT reaches its table's rows — the seek node the thin IR gained when
/// the planner adopted secondary indexes. A closed family: the executor drives
/// exactly these shapes and nothing else.
/// </summary>
internal abstract record SqlAccessPath;
