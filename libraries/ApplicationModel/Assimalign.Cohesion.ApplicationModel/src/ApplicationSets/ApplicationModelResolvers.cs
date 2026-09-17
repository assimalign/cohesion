using System;

namespace Assimalign.Cohesion.ApplicationModel;

/// <summary>
/// Creates AOT-safe application model resolvers for local describe and exported-model discovery.
/// </summary>
public static class ApplicationModelResolvers
{
    /// <summary>Creates a resolver that invokes a gateway executable with <c>--mode describe</c>.</summary>
    /// <param name="executablePath">The executable, apphost, or managed DLL path.</param>
    /// <returns>A local describe resolver.</returns>
    /// <exception cref="ArgumentException">
    /// <paramref name="executablePath"/> is empty.
    /// </exception>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="executablePath"/> is <see langword="null"/>.
    /// </exception>
    public static IApplicationModelResolver Executable(string executablePath) =>
        new ExecutableApplicationModelResolver(executablePath);

    /// <summary>Creates a resolver that reads the model embedded in an application export.</summary>
    /// <param name="exportPath">The <c>export.json</c> path.</param>
    /// <returns>An exported-model resolver.</returns>
    /// <exception cref="ArgumentException"><paramref name="exportPath"/> is empty.</exception>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="exportPath"/> is <see langword="null"/>.
    /// </exception>
    public static IApplicationModelResolver File(string exportPath) =>
        new FileApplicationModelResolver(exportPath);

    /// <summary>Creates a resolver that reads a peer gateway through a supplied control-plane transport.</summary>
    /// <param name="address">The peer gateway control-plane address.</param>
    /// <param name="client">The control-plane transport.</param>
    /// <returns>A peer gateway model resolver.</returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="address"/> or <paramref name="client"/> is <see langword="null"/>.
    /// </exception>
    public static IApplicationModelResolver Gateway(Uri address, IControlPlaneClient client) =>
        new GatewayApplicationModelResolver(address, client);

    /// <summary>
    /// Uses local executable describe in Local and an exported model in other environments.
    /// </summary>
    /// <param name="executablePath">The local gateway executable or managed DLL.</param>
    /// <param name="exportPath">The in-cluster mounted export path.</param>
    /// <returns>An environment-selecting model resolver.</returns>
    /// <exception cref="ArgumentException">
    /// <paramref name="executablePath"/> or <paramref name="exportPath"/> is empty.
    /// </exception>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="executablePath"/> or <paramref name="exportPath"/> is <see langword="null"/>.
    /// </exception>
    public static IApplicationModelResolver ControlPlane(string executablePath, string exportPath) =>
        new EnvironmentApplicationModelResolver(
            new ExecutableApplicationModelResolver(executablePath),
            new FileApplicationModelResolver(exportPath));
}
