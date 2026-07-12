namespace Assimalign.Cohesion.Database.Security;

/// <summary>
/// Factory access to the built-in <see cref="IDatabaseAuthenticator"/> implementations.
/// </summary>
public static class DatabaseAuthenticator
{
    /// <summary>
    /// Gets an authenticator that accepts every principal without verifying any
    /// evidence — the MVP development posture. Production deployments supply a
    /// real implementation through the server options.
    /// </summary>
    public static IDatabaseAuthenticator AllowAll { get; } = new AllowAllDatabaseAuthenticator();
}
