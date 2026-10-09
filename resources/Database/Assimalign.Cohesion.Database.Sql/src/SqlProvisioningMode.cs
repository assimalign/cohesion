namespace Assimalign.Cohesion.Database.Sql;

/// <summary>
/// How a SQL engine's build provisions one declared database (owner decision 55 of 2026-10-09):
/// set per database on <see cref="SqlDatabaseBuilder.Provisioning"/>, never per environment.
/// </summary>
public enum SqlProvisioningMode : byte
{
    /// <summary>
    /// The default. The build creates the database when it does not exist, then plans the
    /// difference between the recorded schema and the declared one and applies it on the schema
    /// session; a schema already applied is skipped.
    /// </summary>
    Apply = 0,

    /// <summary>
    /// The build runs no DDL and creates nothing: the database must exist, and its recorded schema
    /// and live catalog must match the declaration, or the build fails with <c>COHSQLP003</c>. For
    /// pipelines that migrate out of band.
    /// </summary>
    Verify = 1,
}
