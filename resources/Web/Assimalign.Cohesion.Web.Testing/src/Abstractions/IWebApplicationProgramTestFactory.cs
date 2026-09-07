using Assimalign.Cohesion.Hosting;

namespace Assimalign.Cohesion.Web.Testing;

/// <summary>
/// Drives a Web resource's real executable entry point under an isolated ambient context.
/// </summary>
public interface IWebApplicationProgramTestFactory : IWebApplicationTestFactory
{
    /// <summary>Gets the invocation-local resource context supplied to the program.</summary>
    ResourceContext ResourceContext { get; }
}
