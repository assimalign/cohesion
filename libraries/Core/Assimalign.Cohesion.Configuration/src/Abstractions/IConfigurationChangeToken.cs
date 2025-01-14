using System;
using System.Threading;

namespace Assimalign.Cohesion.Configuration;

public interface IConfigurationChangeToken : IChangeToken<IConfiguration>
{
    IDisposable OnAdd(Action<IConfiguration> action);
}
