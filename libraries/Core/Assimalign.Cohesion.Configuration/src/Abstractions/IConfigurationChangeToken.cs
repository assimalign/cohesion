using System;
using System.Threading;

namespace Assimalign.Cohesion.Configuration;

public interface IConfigurationChangeToken : IChangeToken<IConfiguration>
{
    /// <summary>
    /// 
    /// </summary>
    /// <param name="action"></param>
    /// <returns></returns>
    IDisposable OnAdd(Action<IConfiguration> action);
}
