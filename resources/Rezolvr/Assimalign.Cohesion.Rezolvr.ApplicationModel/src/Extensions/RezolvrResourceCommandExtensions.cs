using System;
using System.Net;
using System.Net.Sockets;

namespace Assimalign.Cohesion.ApplicationModel;

/// <summary>Declares records stored by the Rezolvr default control plane.</summary>
public static partial class RezolvrResourceCommandExtensions
{
    extension(IRezolvrResourceDescriptor descriptor)
    {
        /// <summary>Declares an IPv4 address record.</summary>
        /// <param name="name">The DNS record name and ownership key.</param>
        /// <param name="address">The IPv4 address.</param>
        /// <param name="ttlSeconds">The positive record lifetime in seconds.</param>
        /// <param name="optional">Whether rejection may allow dependents to start.</param>
        /// <returns>This descriptor for further declarations.</returns>
        /// <exception cref="ArgumentNullException">The descriptor or address is null.</exception>
        /// <exception cref="ArgumentException">The name, address, or lifetime is invalid.</exception>
        /// <exception cref="InvalidOperationException">The descriptor belongs to a built model.</exception>
        public IRezolvrResourceDescriptor AddARecord(string name, IPAddress address, int ttlSeconds = 300, bool optional = false)
        {
            ArgumentNullException.ThrowIfNull(descriptor);
            ArgumentNullException.ThrowIfNull(address);
            ValidateName(name, nameof(name));
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(ttlSeconds);
            if (address.AddressFamily != AddressFamily.InterNetwork)
            {
                throw new ArgumentException("An A record requires an IPv4 address.", nameof(address));
            }
            descriptor.AddCommand("rezolvr.add-a-record", name,
                new AddARecordCommandPayload(name, address.ToString(), ttlSeconds),
                RezolvrCommandJsonContext.Default.AddARecordCommandPayload, optional);
            return descriptor;
        }

        /// <summary>Declares a canonical-name alias.</summary>
        /// <param name="name">The DNS record name and ownership key.</param>
        /// <param name="target">The canonical DNS target name.</param>
        /// <param name="ttlSeconds">The positive record lifetime in seconds.</param>
        /// <param name="optional">Whether rejection may allow dependents to start.</param>
        /// <returns>This descriptor for further declarations.</returns>
        /// <exception cref="ArgumentNullException">A required argument is null.</exception>
        /// <exception cref="ArgumentException">A name or lifetime is invalid.</exception>
        /// <exception cref="InvalidOperationException">The descriptor belongs to a built model.</exception>
        public IRezolvrResourceDescriptor AddCnameRecord(string name, string target, int ttlSeconds = 300, bool optional = false)
        {
            ArgumentNullException.ThrowIfNull(descriptor);
            ValidateName(name, nameof(name));
            ValidateName(target, nameof(target));
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(ttlSeconds);
            descriptor.AddCommand("rezolvr.add-cname-record", name,
                new AddCnameRecordCommandPayload(name, target, ttlSeconds),
                RezolvrCommandJsonContext.Default.AddCnameRecordCommandPayload, optional);
            return descriptor;
        }
    }

    private static void ValidateName(string value, string parameter)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameter);
        if (value.Contains('/') || Uri.CheckHostName(value.TrimEnd('.')) != UriHostNameType.Dns)
        {
            throw new ArgumentException("Record names must be DNS names without '/'.", parameter);
        }
    }
}
