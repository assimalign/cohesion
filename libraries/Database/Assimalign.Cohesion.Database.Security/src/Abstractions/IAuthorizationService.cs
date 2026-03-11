using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Database.Security;

/// <summary>
/// Evaluates whether a principal can perform an action on a resource.
/// </summary>
public interface IAuthorizationService
{
    /// <summary>
    /// Evaluates an authorization decision.
    /// </summary>
    /// <param name="principalId">Principal identifier.</param>
    /// <param name="resource">Resource identifier.</param>
    /// <param name="action">Action name.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>True when access is granted.</returns>
    ValueTask<bool> AuthorizeAsync(string principalId, string resource, string action, CancellationToken cancellationToken = default);
}
