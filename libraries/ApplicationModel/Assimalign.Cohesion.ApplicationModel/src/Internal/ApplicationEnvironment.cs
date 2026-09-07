using System;

namespace Assimalign.Cohesion.ApplicationModel;

/// <summary>
/// The default <see cref="IApplicationEnvironment"/>, resolved from the host process
/// by <see cref="AppEnvironment"/>.
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
        return FromName(AppEnvironment.GetEnvironmentName());
    }

    public static ApplicationEnvironment FromName(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        bool isDevelopment = string.Equals(name, DevelopmentEnvironment, StringComparison.OrdinalIgnoreCase);

        return new ApplicationEnvironment(name, isDevelopment);
    }
}
