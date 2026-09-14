namespace Assimalign.Cohesion;

public static partial class AppEnvironment
{
    /// <summary>
    /// Contains the environment-variable names and defaults used to resolve the application environment.
    /// </summary>
    public static class Keys
    {
        /// <summary>
        /// Gets the Cohesion-specific environment-name variable.
        /// </summary>
        public const string EnvironmentKey = Core.ResourceEnvironment.Environment;

        /// <summary>
        /// Gets the .NET environment-name fallback variable.
        /// </summary>
        public const string DotNetEnvironmentKey = "DOTNET_ENVIRONMENT";

        /// <summary>
        /// Gets the environment name for an explicitly selected developer-machine invocation.
        /// </summary>
        public const string Local = "Local";

        /// <summary>
        /// Gets the environment name for a deployable development environment.
        /// </summary>
        public const string Development = "Development";

        /// <summary>
        /// Gets the environment name for a staging deployment.
        /// </summary>
        public const string Staging = "Staging";

        /// <summary>
        /// Gets the environment name for a production deployment.
        /// </summary>
        public const string Production = "Production";

        /// <summary>
        /// Gets the environment name used when neither environment variable is set.
        /// </summary>
        public const string DefaultEnvironmentName = Production;
    }
}
