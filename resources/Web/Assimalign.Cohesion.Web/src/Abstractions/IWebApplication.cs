using System;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Web;

using Assimalign.Cohesion.Hosting;

/// <summary>
/// Represents an abstraction of a web server.
/// </summary>
public interface IWebApplication : IHost
{
    /// <summary>
    /// 
    /// </summary>
    new IWebApplicationContext Context { get; }
}
