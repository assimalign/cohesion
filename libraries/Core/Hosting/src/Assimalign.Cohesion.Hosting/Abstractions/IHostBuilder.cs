namespace Assimalign.Cohesion.Hosting;

/// <summary>
/// A builder pattern for creating a <see cref="IHost"/>.
/// </summary>
public interface IHostBuilder
{
    /// <summary>
    /// Adds a 
    /// </summary>
    /// <param name="server"></param>
    /// <returns></returns>
    IHostBuilder AddServer(IHostServer server);
    /// <summary>
    /// 
    /// </summary>
    /// <param name="builder"></param>
    /// <returns></returns>
    IHostBuilder AddServer(IHostServerBuilder builder);
    /// <summary>
    /// 
    /// </summary>
    /// <param name="callback"></param>
    /// <returns></returns>
    IHostBuilder AddServerStateCallback(HostServerStateCallbackAsync callback);
    /// <summary>
    /// 
    /// </summary>
    /// <returns></returns>
    IHost Build();
}