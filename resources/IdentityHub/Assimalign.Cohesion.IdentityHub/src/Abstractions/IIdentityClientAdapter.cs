using System;

namespace Assimalign.Cohesion.IdentityHub;

/// <summary>
/// 
/// </summary>
public interface IIdentityClientAdapter
{
    /// <summary>
    /// 
    /// </summary>
    /// <returns></returns>
    IIdentityClientContext GetContext();
}
