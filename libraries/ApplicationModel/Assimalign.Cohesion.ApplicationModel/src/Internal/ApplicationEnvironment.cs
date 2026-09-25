using System;

namespace Assimalign.Cohesion.ApplicationModel.Internal;

/// <summary>
/// The default <see cref="IApplicationEnvironment"/>, resolved from the host process
/// by <see cref="AppEnvironment"/>.
/// </summary>
internal sealed class ApplicationEnvironment : IApplicationEnvironment
{
    public ApplicationEnvironment(EnvironmentName name, bool isLocal, bool isDevelopment)
    {
        Name = name;
        IsLocal = isLocal;
        IsDevelopment = isDevelopment;
    }

    public EnvironmentName Name { get; }

    public bool IsLocal { get; }

    public bool IsDevelopment { get; }

    public static ApplicationEnvironment FromHost()
    {
        // Apphosts default to Local before gateway selection. Core's resource-runtime
        // default remains Production; preserve every explicit host value unchanged.
        bool isUnset = Environment.GetEnvironmentVariable(AppEnvironment.Keys.EnvironmentKey) is null
            && Environment.GetEnvironmentVariable(AppEnvironment.Keys.DotNetEnvironmentKey) is null;
        return Create(isUnset ? AppEnvironment.Keys.Local : AppEnvironment.GetEnvironmentName());
    }

    public static ApplicationEnvironment FromName(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        return Create(name);
    }

    private static ApplicationEnvironment Create(string name)
    {
        bool isLocal = string.Equals(name, AppEnvironment.Keys.Local, StringComparison.OrdinalIgnoreCase);
        bool isDevelopment = string.Equals(name, AppEnvironment.Keys.Development, StringComparison.OrdinalIgnoreCase);

        return new ApplicationEnvironment(name, isLocal, isDevelopment);
    }
}
