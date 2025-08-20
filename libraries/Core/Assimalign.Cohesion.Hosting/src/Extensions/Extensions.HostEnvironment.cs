namespace Assimalign.Cohesion.Hosting;

using Cohesion.Internal;

public static class HostEnvironmentExtensions
{
    private static readonly string development = nameof(development);
    private static readonly string staging = nameof(staging);
    private static readonly string test = nameof(test);
    private static readonly string production = nameof(production);
    private static readonly string uat = nameof(uat);
    private static readonly string qa = nameof(qa);
    private static readonly string sandbox = nameof(sandbox);

    extension(IHostEnvironment environment)
    {
        public bool IsDevelopment()
        {
            return ThrowHelper.ThrowIfNull(environment).IsEnvironment(development);
        }

        public bool IsStaging()
        {
            return ThrowHelper.ThrowIfNull(environment).IsEnvironment(staging);
        }

        public bool IsTest()
        {
            return ThrowHelper.ThrowIfNull(environment).IsEnvironment(test);
        }
        public bool IsProduction()
        {
            return ThrowHelper.ThrowIfNull(environment).IsEnvironment(production);
        }
        public bool IsUserAcceptanceTesting()
        {
           
            return ThrowHelper.ThrowIfNull(environment).IsEnvironment(uat);
        }
        public bool IsQualityAssurance()
        {
            return ThrowHelper.ThrowIfNull(environment).IsEnvironment(qa);
        }
    }
}
