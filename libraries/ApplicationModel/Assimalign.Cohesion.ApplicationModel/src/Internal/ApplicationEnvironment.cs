using System;

namespace Assimalign.Cohesion.ApplicationModel;

/// <summary>
/// The default <see cref="IApplicationEnvironment"/>, resolved from the host process
/// environment variables <c>COHESION_ENVIRONMENT</c> or <c>DOTNET_ENVIRONMENT</c>,
/// defaulting to <c>Production</c>.
/// </summary>
internal sealed class ApplicationEnvironment : IApplicationEnvironment
{
    private const string DevelopmentEnvironment = "Development";

    public ApplicationEnvironment(EnvironmentName name, bool isDevelopment)
    {
        Name = name;
        IsDevelopment = isDevelopment;
    }

    public EnvironmentName Name { get; }

    public bool IsDevelopment { get; }

    public static ApplicationEnvironment FromHost()
    {
        string name =
            Environment.GetEnvironmentVariable("COHESION_ENVIRONMENT")
            ?? Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT")
            ?? "Production";

        bool isDevelopment = string.Equals(name, DevelopmentEnvironment, StringComparison.OrdinalIgnoreCase);

        return new ApplicationEnvironment(name, isDevelopment);
    }
}
