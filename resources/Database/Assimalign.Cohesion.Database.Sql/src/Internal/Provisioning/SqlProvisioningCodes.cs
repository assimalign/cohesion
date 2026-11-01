namespace Assimalign.Cohesion.Database.Sql.Internal;

/// <summary>
/// The codes that lead the message of each provisioning failure of a SQL engine's declared
/// databases (B1 of the engine extensibility design), as <c>COHSQLE</c> and <c>COHSQLT</c> lead the
/// evaluation and transaction failures. Each failure is a <c>SqlSchemaMigrationException</c> that
/// names the engine and the database.
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

    /// <summary>
    /// A step of a schema apply failed; the completed reversible steps were compensated.
    /// </summary>
    internal const string StepFailed = "CS03104";

    /// <summary>Unpublished joint preparation failed; the old applied schema remains usable.</summary>
    internal const string JointFailed = "CS03402";

    /// <summary>
    /// An apply refused before any step runs: its plan needs a destructive step the schema does
    /// not allow or a change the planner cannot make, or the schema would adopt a table or index it
    /// did not create.
    /// </summary>
    internal const string PolicyRefused = "CS03105";
}
