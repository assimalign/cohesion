namespace Assimalign.Cohesion.Hosting;

public static class HostExtensions
{
    /// <summary>
    /// Runs the <see cref="IHost"/> synchronously.
    /// </summary>
    /// <param name="host"></param>
    public static void Run(this IHost host)
    {
        host.RunAsync().GetAwaiter().GetResult();
    }
}
