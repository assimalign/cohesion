using System.Security.Claims;

namespace Assimalign.Cohesion.Http;

/// <summary>
/// 
/// </summary>
public interface IHttpIdentityFeature : IHttpFeature
{
    /// <summary>
    /// 
    /// </summary>
    ClaimsPrincipal ClaimsPrincipal { get; }
}
