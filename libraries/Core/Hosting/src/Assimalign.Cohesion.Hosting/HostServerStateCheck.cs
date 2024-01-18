using System.Threading.Tasks;

namespace Assimalign.Cohesion.Hosting;

/// <summary>
/// This callback is used to check the state of am encapsulated server.
/// </summary>
/// <remarks>
/// Use this for 
/// </remarks>
/// <param name="state"></param>
public delegate Task HostServerStateCallbackAsync(IHostServerState state);
