using System;

namespace Assimalign.Cohesion.Transports;

/// <summary>
/// 
/// </summary>
public interface ITransportContext
{
    /// <summary>
    /// 
    /// </summary>
    ITransportConnection Connection { get; }

    /// <summary>
    /// 
    /// </summary>
    IServiceProvider? ServiceProvider { get; }
}