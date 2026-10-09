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
    /// A declaration the engine's build refuses before any file is touched (phase 3): a schema
    /// that does not compile, or declares a principal or a custom type.
    /// </summary>
    internal const string DeclarationRefused = "COHSQLP001";

    /// <summary>
    /// An existing database whose default collation is not the declared one: a key's collation id
    /// is persisted in it and cannot change.
    /// </summary>
    internal const string CollationMismatch = "COHSQLP002";

    /// <summary>
    /// A database declared in <see cref="SqlProvisioningMode.Verify"/> mode that does not exist or
    /// does not hold exactly the declared schema.
    /// </summary>
    internal const string Drift = "COHSQLP003";

    /// <summary>
    /// A step of a schema apply failed; the completed reversible steps were compensated.
    /// </summary>
    internal const string StepFailed = "COHSQLP004";
}
