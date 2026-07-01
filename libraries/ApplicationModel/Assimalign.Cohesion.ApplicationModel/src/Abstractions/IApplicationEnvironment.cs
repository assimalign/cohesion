namespace Assimalign.Cohesion.ApplicationModel;

/// <summary>
/// Describes the environment an application is being realized into. Gateways read it to
/// choose behavior — for example a daemon-load fast path and relaxed readiness in development.
/// </summary>
public interface IApplicationEnvironment
{
    /// <summary>
    /// The environment name, for example <c>Development</c> or <c>Production</c>.
    /// </summary>
    EnvironmentName Name { get; }

    /// <summary>
    /// <see langword="true"/> when realizing for local development.
    /// </summary>
    bool IsDevelopment { get; }
}
