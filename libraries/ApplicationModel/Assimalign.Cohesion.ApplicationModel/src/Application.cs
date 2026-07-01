namespace Assimalign.Cohesion.ApplicationModel;

/// <summary>
/// The entry point for composing an application model. Mirrors the
/// <c>WebApplication.CreateBuilder()</c> idiom.
/// </summary>
public static class Application
{
    /// <summary>
    /// Creates a new application builder.
    /// </summary>
    /// <returns>A new <see cref="IApplicationBuilder"/>.</returns>
    public static IApplicationBuilder CreateBuilder() => new ApplicationBuilder();

    /// <summary>
    /// Creates a new application builder with the process command-line arguments.
    /// </summary>
    /// <param name="args">The process command-line arguments.</param>
    /// <returns>A new <see cref="IApplicationBuilder"/>.</returns>
    public static IApplicationBuilder CreateBuilder(string[] args) => new ApplicationBuilder(args);
}
