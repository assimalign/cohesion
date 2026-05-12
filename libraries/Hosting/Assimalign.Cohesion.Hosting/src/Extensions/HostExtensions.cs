using System;

namespace Assimalign.Cohesion.Hosting;

using Internal;

public static partial class HostExtensions
{
    extension<TContext>(Host<TContext> host) where TContext : HostContext
    {
        /// <summary>
        /// Runs the <see cref="IHost"/> synchronously.
        /// </summary>
        public void Run()
        {
            host.RunAsync().GetAwaiter().GetResult();
        }

        /// <summary>
        /// Converts a <see cref="IHost"/> to run as a <see cref="IHostService"/>.
        /// </summary>
        /// <returns></returns>
        public IHostService AsService()
        {
            ArgumentNullException.ThrowIfNull(host);

            return new HostToServiceWrapper(host);
        }
    }

    extension(IHostEnvironment environment)
    {
        public bool IsEnvironment(string name)
        {
            return environment.Name == name;
        }

        public bool IsDevelopment()
        {
            return environment.IsEnvironment("development");
        }

        public bool IsStaging()
        {
            return environment.IsEnvironment("staging");
        }

        public bool IsTest()
        {
            return environment.IsEnvironment("test");
        }

        public bool IsProduction()
        {
            return environment.IsEnvironment("production");
        }

        public bool IsUserAcceptanceTesting()
        {
            return environment.IsEnvironment("uat");
        }

        public bool IsQualityAssurance()
        {
            return environment.IsEnvironment("qa");
        }
    }
}