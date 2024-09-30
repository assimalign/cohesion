namespace Assimalign.Cohesion.Hosting;

using Assimalign.Cohesion.Hosting.Internal;

public static class HostEnvironmentExtensions
{
    private static readonly string development = nameof(development);
    private static readonly string staging = nameof(staging);
    private static readonly string test = nameof(test);
    private static readonly string production = nameof(production);
    private static readonly string uat = nameof(uat);
    private static readonly string qa = nameof(qa);

    public static bool IsDevelopment(this IHostEnvironment environment)
    {
        ThrowIfNull(environment);
        return environment.IsEnvironment(development);
    }

    public static bool IsStaging(this IHostEnvironment environment)
    {
        ThrowIfNull(environment);
        return environment.IsEnvironment(staging);
    }
    public static bool IsTest(this IHostEnvironment environment)
    {
        ThrowIfNull(environment);
        return environment.IsEnvironment(test);
    }
    public static bool IsProduction(this IHostEnvironment environment)
    {
        ThrowIfNull(environment);
        return environment.IsEnvironment(production);
    }
    public static bool IsUat(this IHostEnvironment environment)
    {
        ThrowIfNull(environment);
        return environment.IsEnvironment(uat);
    }
    public static bool IsQa(this IHostEnvironment environment)
    {
        ThrowIfNull(environment);
        return environment.IsEnvironment(qa);
    }

    private static void ThrowIfNull(IHostEnvironment environment)
    {
        if (environment is null)
        {
            ThrowHelper.ThrowArgumentNullException(nameof(environment));
        }
    }
}
