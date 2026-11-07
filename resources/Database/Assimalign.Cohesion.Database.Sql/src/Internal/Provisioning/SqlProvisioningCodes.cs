namespace Assimalign.Cohesion.Database.Sql.Internal;

/// <summary>
/// Stable identities for provisioning a SQL engine's declared databases. Exceptions carry their
/// code through diagnostics; held declarations report their code and detail through Provisioning
/// while the database remains available at the stored schema.
/// </summary>
internal static class SqlProvisioningCodes
{
    /// <summary>
    /// A declaration the engine's build refuses before any file is touched (phases 2 and 3): a schema
    /// that does not compile, declares a principal, declares a domain that conflicts with a type the
    /// engine registers, or declares a column of a type the engine's catalog does not resolve.
    /// </summary>
    internal const string DeclarationRefused = "CS03101";

    /// <summary>
    /// An existing database whose default collation is not the declared one: a key's collation id
    /// is persisted in it and cannot change.
    /// </summary>
    internal const string CollationMismatch = "CS03102";

    /// <summary>
    /// A database declared in <see cref="SqlProvisioningMode.Verify"/> mode that does not exist or
    /// does not hold exactly the declared schema.
    /// </summary>
    internal const string Drift = "CS03103";

    // CS03104 retired: per-step publication and compensation were removed (#1778).

    /// <summary>Unpublished joint preparation failed; the old applied schema remains usable.</summary>
    internal const string JointFailed = "CS03402";

    /// <summary>Publication has an unconfirmed commit; recovery decides the schema.</summary>
    internal const string JointUnconfirmed = "CS03403";

    /// <summary>A pending joint migration reserves an unpublished table name.</summary>
    internal const string JointNameReserved = "CS03404";

    /// <summary>An earlier applied declaration would lose data; Build retained the stored schema.</summary>
    internal const string RollbackHeld = "CS03405";

    /// <summary>
    /// An apply refused before any step runs: its plan needs a destructive step the schema does
    /// not allow or a change the planner cannot make, or the schema would adopt a table or index it
    /// did not create.
    /// </summary>
    internal const string PolicyRefused = "CS03105";
}
