namespace Assimalign.Cohesion.ApplicationModel;

/// <summary>
/// Describes the environment an application is being realized into. Gateways reserve
/// developer-machine behavior for the Local environment.
/// </summary>
public interface IApplicationEnvironment
{
    /// <summary>
    /// The environment name, for example <c>Local</c>, <c>Development</c>, or <c>Production</c>.
    /// </summary>
    EnvironmentName Name { get; }

    /// <summary>
    /// <see langword="true"/> when realizing on a developer machine in the Local environment.
    /// </summary>
    bool IsLocal { get; }

    /// <summary>
    /// <see langword="true"/> for the named deployed Development environment, which retains strict behavior.
    /// </summary>
    bool IsDevelopment { get; }
}
