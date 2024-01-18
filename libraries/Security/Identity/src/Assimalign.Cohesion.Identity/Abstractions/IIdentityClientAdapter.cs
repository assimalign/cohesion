using System;

namespace Assimalign.Cohesion.Net.Identity;

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
