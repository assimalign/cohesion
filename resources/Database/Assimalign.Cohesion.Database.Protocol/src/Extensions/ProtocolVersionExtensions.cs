namespace Assimalign.Cohesion.Database.Protocol;

/// <summary>
/// Protocol-package extensions for <see cref="ProtocolVersion"/>.
/// </summary>
public static class ProtocolVersionExtensions
{
    extension(ProtocolVersion)
    {
        /// <summary>
        /// Gets the current protocol version implemented by this assembly.
        /// </summary>
        public static ProtocolVersion Current => new(1, 0);
    }
}
