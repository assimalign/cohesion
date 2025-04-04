using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Transports;

public interface ITransportConnection<T> : ITransportConnection
{
    /// <summary>
    /// 
    /// </summary>
    new T ConnectionData { get; }
}
