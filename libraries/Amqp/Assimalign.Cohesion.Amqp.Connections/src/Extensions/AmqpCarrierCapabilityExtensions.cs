using System;

using Assimalign.Cohesion.Connections;

namespace Assimalign.Cohesion.Amqp.Connections;

/// <summary>
/// Provides AMQP carrier validation extension members for <see cref="ConnectionCapabilities"/>.
/// </summary>
internal static class AmqpCarrierCapabilityExtensions
{
    extension(ConnectionCapabilities capabilities)
    {
        /// <summary>
        /// Throws when the capabilities do not describe a carrier usable for AMQP framing:
        /// a reliable, ordered byte stream.
        /// </summary>
        /// <param name="paramName">The name of the constructor parameter that supplied the carrier.</param>
        /// <exception cref="ArgumentException">Thrown when the carrier is not a reliable, ordered byte stream.</exception>
        public void ThrowIfNotAmqpCarrier(string paramName)
        {
            if (capabilities is not { Delivery: ConnectionDelivery.Stream, IsReliable: true, IsOrdered: true })
            {
                throw new ArgumentException(
                    "The AMQP transport requires a carrier that delivers a reliable, ordered byte stream.",
                    paramName);
            }
        }
    }
}
