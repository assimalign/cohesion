using System;

namespace Assimalign.Cohesion;

public static partial class AppEnvironment
{
    /// <summary>
    /// Defines the frozen version 1 environment-variable contract shared by Cohesion gateways and resources.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Each name is a <see langword="const"/> and is inlined into consuming assemblies, so changing a
    /// value is a breaking wire-contract change. <c>docs/RUNTIME_CONTRACT.md</c> is the language-neutral
    /// statement of the same contract; the Core runtime-contract tests hold this file's constants and
    /// that document in lockstep, and reject any line containing <c>COHESION_</c> (code or comment)
    /// elsewhere in the <c>src/</c> trees of <c>libraries/Core</c>, <c>libraries/Hosting</c>, and
    /// <c>libraries/ApplicationModel</c>.
    /// </para>
    /// <para>
    /// This class is the contract's only .NET home. Changes to <see cref="AppEnvironment"/> must keep it
    /// where it is and as it is.
    /// </para>
    /// </remarks>
    public static class Variables
    {
        /// <summary>Gets the application identity variable.</summary>
        public const string Application = "COHESION_APPLICATION";

        /// <summary>Gets the resource identity variable.</summary>
        public const string Resource = "COHESION_RESOURCE";

        /// <summary>Gets the gateway topology variable.</summary>
        public const string Gateway = "COHESION_GATEWAY";

        /// <summary>Gets the application environment-name variable.</summary>
        public const string Environment = "COHESION_ENVIRONMENT";

        /// <summary>Gets the resource content-root variable.</summary>
        public const string ContentRoot = "COHESION_CONTENT_ROOT";

        /// <summary>Gets the application public trust-key variable.</summary>
        public const string ApplicationTrustKey = "COHESION_APPLICATION_TRUST_KEY";

        /// <summary>Gets the declared endpoint host variable pattern.</summary>
        public const string EndpointHostPattern = "COHESION_ENDPOINT_<EP>_HOST";

        /// <summary>Gets the declared endpoint port variable pattern.</summary>
        public const string EndpointPortPattern = "COHESION_ENDPOINT_<EP>_PORT";

        /// <summary>Gets the declared endpoint scheme variable pattern.</summary>
        public const string EndpointSchemePattern = "COHESION_ENDPOINT_<EP>_SCHEME";

        /// <summary>Gets the declared endpoint public URL variable pattern.</summary>
        public const string EndpointPublicUrlPattern = "COHESION_ENDPOINT_<EP>_PUBLIC_URL";

        /// <summary>Gets the dependency URL variable pattern.</summary>
        public const string DependencyUrlPattern = "COHESION_DEPENDENCY_<RES>_<EP>_URL";

        /// <summary>Gets the dependency host variable pattern.</summary>
        public const string DependencyHostPattern = "COHESION_DEPENDENCY_<RES>_<EP>_HOST";

        /// <summary>Gets the dependency port variable pattern.</summary>
        public const string DependencyPortPattern = "COHESION_DEPENDENCY_<RES>_<EP>_PORT";

        /// <summary>Gets the dependency scheme variable pattern.</summary>
        public const string DependencySchemePattern = "COHESION_DEPENDENCY_<RES>_<EP>_SCHEME";

        /// <summary>Gets the resource mount path variable pattern.</summary>
        public const string MountPathPattern = "COHESION_MOUNT_<M>_PATH";

        /// <summary>Gets the configuration bridge variable pattern.</summary>
        public const string ConfigurationPattern = "COHESION_CONFIG__<Section>__<Key>";

        /// <summary>Gets the bootstrap credential file-path variable.</summary>
        public const string BootstrapTokenPath = "COHESION_BOOTSTRAP_TOKEN_PATH";

        /// <summary>Gets the transport trust-anchor bundle file-path variable.</summary>
        public const string TrustBundlePath = "COHESION_TRUST_BUNDLE_PATH";

        /// <summary>Gets the Windows graceful-stop event variable.</summary>
        public const string StopEvent = "COHESION_STOP_EVENT";

        /// <summary>Gets the optional OpenTelemetry collector endpoint variable.</summary>
        public const string TelemetryEndpoint = "COHESION_TELEMETRY_ENDPOINT";

        /// <summary>Gets the optional OpenTelemetry transport protocol variable.</summary>
        public const string TelemetryProtocol = "COHESION_TELEMETRY_PROTOCOL";

        /// <summary>Gets the optional OpenTelemetry headers file-path variable.</summary>
        public const string TelemetryHeadersPath = "COHESION_TELEMETRY_HEADERS_PATH";

        /// <summary>Gets the optional structured log format variable.</summary>
        public const string LogFormat = "COHESION_LOG_FORMAT";

        /// <summary>
        /// Builds a declared-endpoint environment-variable name.
        /// </summary>
        /// <param name="endpoint">The declared endpoint name.</param>
        /// <param name="suffix"><c>HOST</c>, <c>PORT</c>, <c>SCHEME</c>, or <c>PUBLIC_URL</c>.</param>
        /// <returns>The canonical endpoint variable name.</returns>
        /// <exception cref="ArgumentException">
        /// Thrown when <paramref name="endpoint"/> or <paramref name="suffix"/> is empty.
        /// </exception>
        /// <exception cref="ArgumentOutOfRangeException">
        /// Thrown when <paramref name="suffix"/> is not a supported endpoint suffix.
        /// </exception>
        public static string Endpoint(string endpoint, string suffix)
        {
            string normalizedEndpoint = NormalizeName(endpoint);
            string normalizedSuffix = NormalizeName(suffix);
            string pattern = normalizedSuffix switch
            {
                "HOST" => EndpointHostPattern,
                "PORT" => EndpointPortPattern,
                "SCHEME" => EndpointSchemePattern,
                "PUBLIC_URL" => EndpointPublicUrlPattern,
                _ => throw new ArgumentOutOfRangeException(
                    nameof(suffix),
                    suffix,
                    "The endpoint suffix must be HOST, PORT, SCHEME, or PUBLIC_URL.")
            };

            return pattern.Replace("<EP>", normalizedEndpoint, StringComparison.Ordinal);
        }

        /// <summary>
        /// Builds a dependency endpoint environment-variable name.
        /// </summary>
        /// <param name="resource">The dependency resource name.</param>
        /// <param name="endpoint">The dependency endpoint name.</param>
        /// <param name="suffix"><c>URL</c>, <c>HOST</c>, <c>PORT</c>, or <c>SCHEME</c>.</param>
        /// <returns>The canonical dependency variable name.</returns>
        /// <exception cref="ArgumentException">
        /// Thrown when <paramref name="resource"/>, <paramref name="endpoint"/>, or
        /// <paramref name="suffix"/> is empty.
        /// </exception>
        /// <exception cref="ArgumentOutOfRangeException">
        /// Thrown when <paramref name="suffix"/> is not a supported dependency suffix.
        /// </exception>
        public static string Dependency(string resource, string endpoint, string suffix)
        {
            string normalizedResource = NormalizeName(resource);
            string normalizedEndpoint = NormalizeName(endpoint);
            string normalizedSuffix = NormalizeName(suffix);
            string pattern = normalizedSuffix switch
            {
                "URL" => DependencyUrlPattern,
                "HOST" => DependencyHostPattern,
                "PORT" => DependencyPortPattern,
                "SCHEME" => DependencySchemePattern,
                _ => throw new ArgumentOutOfRangeException(
                    nameof(suffix),
                    suffix,
                    "The dependency suffix must be URL, HOST, PORT, or SCHEME.")
            };

            return pattern
                .Replace("<RES>", normalizedResource, StringComparison.Ordinal)
                .Replace("<EP>", normalizedEndpoint, StringComparison.Ordinal);
        }

        /// <summary>
        /// Builds a resource mount path environment-variable name.
        /// </summary>
        /// <param name="mount">The declared mount name.</param>
        /// <returns>The canonical mount path variable name.</returns>
        /// <exception cref="ArgumentException">Thrown when <paramref name="mount"/> is empty.</exception>
        public static string Mount(string mount)
        {
            return MountPathPattern.Replace("<M>", NormalizeName(mount), StringComparison.Ordinal);
        }

        /// <summary>
        /// Builds a configuration bridge environment-variable name.
        /// </summary>
        /// <param name="section">The configuration section name.</param>
        /// <param name="key">The configuration key name.</param>
        /// <returns>The configuration variable name, using a double underscore as the section separator.</returns>
        /// <exception cref="ArgumentException">
        /// Thrown when <paramref name="section"/> or <paramref name="key"/> is empty.
        /// </exception>
        public static string Configuration(string section, string key)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(section);
            ArgumentException.ThrowIfNullOrWhiteSpace(key);

            return ConfigurationPattern
                .Replace("<Section>", section, StringComparison.Ordinal)
                .Replace("<Key>", key, StringComparison.Ordinal);
        }

        private static string NormalizeName(string value)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(value);

            char[] normalized = new char[value.Length];
            for (int index = 0; index < value.Length; index++)
            {
                char character = value[index];
                normalized[index] = character switch
                {
                    >= 'a' and <= 'z' => (char)(character - ('a' - 'A')),
                    >= 'A' and <= 'Z' or >= '0' and <= '9' => character,
                    _ => '_'
                };
            }

            return new string(normalized);
        }
    }
}
