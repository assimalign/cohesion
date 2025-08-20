namespace Assimalign.Cohesion.Hosting;

using Internal;

public static class HostExtensions
{
    extension<TContext>(Host<TContext> host) where TContext : HostContext
    {
        /// <summary>
        /// Runs the <see cref="IHost"/> synchronously.
        /// </summary>
        /// <param name="host"></param>
        public void Run()
        {
            host.RunAsync().GetAwaiter().GetResult();
        }

        /// <summary>
        /// 
        /// </summary>
        /// <returns></returns>
        public IHostService AsService()
        {
            return new HostToServiceWrapper(host);
        }
    }
}
