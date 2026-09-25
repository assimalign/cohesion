using System;

using Assimalign.Cohesion.ApplicationModel.Gateway.Internal;

namespace Assimalign.Cohesion.ApplicationModel.Gateway;

/// <summary>
/// Adds gateway-owned opaque resources to an <see cref="IApplicationBuilder"/>.
/// </summary>
public static class ApplicationGatewayResourceExtensions
{
    private const string Sha256Marker = "@sha256:";
    private const int Sha256HexLength = 64;

    extension(IApplicationBuilder builder)
    {
        /// <summary>
        /// Adds an opaque container image as a deployment planned by <see cref="GenericPlanner"/>.
        /// </summary>
        /// <param name="name">The resource name.</param>
        /// <param name="imageReference">The image reference, pinned by a SHA-256 digest.</param>
        /// <param name="configure">
        /// Configures the container's endpoints, explicit probes, environment, and restart policy.
        /// </param>
        /// <returns>The resource descriptor, for declaring dependencies.</returns>
        /// <exception cref="ArgumentException">
        /// <paramref name="imageReference"/> is empty or is not pinned by a 64-digit SHA-256 digest.
        /// </exception>
        /// <exception cref="ArgumentNullException">
        /// <paramref name="builder"/> or <paramref name="configure"/> is <see langword="null"/>.
        /// </exception>
        /// <exception cref="InvalidOperationException">
        /// No endpoint or no enabled readiness probe was configured, or a configured endpoint or probe is invalid.
        /// </exception>
        public IApplicationResourceDescriptor AddContainer(
            ResourceName name,
            string imageReference,
            Action<IContainerResourceOptionsBuilder> configure)
        {
            ArgumentNullException.ThrowIfNull(builder);
            ArgumentNullException.ThrowIfNull(configure);
            ValidateImageReference(imageReference);

            var options = new ContainerResourceOptionsBuilder();
            configure(options);
            options.Validate();

            return builder.AddResource(model =>
                new ContainerResource(model.Name, name, imageReference, options));
        }
    }

    private static void ValidateImageReference(string imageReference)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(imageReference);

        int markerIndex = imageReference.LastIndexOf(Sha256Marker, StringComparison.Ordinal);
        int digestIndex = markerIndex + Sha256Marker.Length;
        if (markerIndex <= 0 || imageReference.Length - digestIndex != Sha256HexLength)
        {
            ThrowInvalidImageReference(imageReference);
        }

        ReadOnlySpan<char> digest = imageReference.AsSpan(digestIndex, Sha256HexLength);
        foreach (char character in digest)
        {
            if (!IsHexDigit(character))
            {
                ThrowInvalidImageReference(imageReference);
            }
        }
    }

    private static bool IsHexDigit(char character)
        => character is >= '0' and <= '9'
            or >= 'a' and <= 'f'
            or >= 'A' and <= 'F';

    private static void ThrowInvalidImageReference(string imageReference)
        => throw new ArgumentException(
            $"Container image '{imageReference}' must end with '@sha256:' followed by 64 hexadecimal digits.",
            nameof(imageReference));
}
