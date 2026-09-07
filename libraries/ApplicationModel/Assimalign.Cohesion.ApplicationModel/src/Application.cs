namespace Assimalign.Cohesion.ApplicationModel;

/// <summary>
/// The entry point for composing an application model. Mirrors the
/// <c>WebApplication.CreateBuilder()</c> idiom.
/// </summary>
public static class Application
{
    /// <summary>
    /// Creates a new application builder using the legacy <c>application</c> identity.
    /// </summary>
    /// <remarks>
    /// Generated gateways use <see cref="CreateBuilder(ApplicationName, string[])"/> so their
    /// compiled identity is explicit. This overload remains for existing hand-authored callers.
    /// </remarks>
    /// <returns>A new <see cref="IApplicationBuilder"/>.</returns>
    public static IApplicationBuilder CreateBuilder() => new ApplicationBuilder();

    /// <summary>
    /// Creates a new application builder with the process command-line arguments.
    /// </summary>
    /// <param name="args">The process command-line arguments.</param>
    /// <returns>A new <see cref="IApplicationBuilder"/>.</returns>
    /// <exception cref="System.ArgumentException">
    /// A recognized gateway command-line option is missing or has an invalid value.
    /// </exception>
    /// <remarks>
    /// An unnamed builder derives a slug from the entry assembly only in Development. In
    /// other environments, call <see cref="IApplicationBuilder.UseName(ApplicationName)"/> before building.
    /// </remarks>
    public static IApplicationBuilder CreateBuilder(string[] args) => new ApplicationBuilder(args);

    /// <summary>
    /// Creates a new application builder with a compiled application identity and the
    /// process command-line arguments.
    /// </summary>
    /// <param name="name">The application identity compiled into the gateway.</param>
    /// <param name="args">The process command-line arguments.</param>
    /// <returns>A new <see cref="IApplicationBuilder"/>.</returns>
    /// <exception cref="System.ArgumentException">
    /// A recognized gateway command-line option is missing or has an invalid value.
    /// </exception>
    /// <remarks>
    /// The name is supplied directly to the builder; no assembly attribute is inspected at runtime.
    /// </remarks>
    public static IApplicationBuilder CreateBuilder(ApplicationName name, string[] args) =>
        new ApplicationBuilder(name, args);
}
