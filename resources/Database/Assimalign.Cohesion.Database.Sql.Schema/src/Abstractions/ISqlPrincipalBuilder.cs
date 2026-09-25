using System;

namespace Assimalign.Cohesion.Database.Sql.Schema;

/// <summary>Configures grants for a database-scoped principal.</summary>
public interface ISqlPrincipalBuilder
{
    /// <summary>Grants a permission over named schema objects.</summary>
    /// <param name="permission">The granted permission.</param>
    /// <param name="objects">The schema object names.</param>
    /// <exception cref="ArgumentException">No object is provided, or an object name is empty or whitespace.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="objects"/> or one of its entries is null.</exception>
    void Grant(SqlPermission permission, params string[] objects);
}
