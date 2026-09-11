using System;
using System.Text.Json;

namespace Assimalign.Cohesion.ConfigurationStore.ApplicationModel;

/// <summary>Declares configuration values for the target's default control plane.</summary>
public static partial class ConfigurationStoreResourceCommandExtensions
{
    extension(IConfigurationStoreResourceDescriptor descriptor)
    {
        /// <summary>Declares a string or null JSON configuration value in a namespace.</summary>
        /// <param name="namespaceName">The namespace name.</param>
        /// <param name="key">The configuration key within the namespace, without slash characters.</param>
        /// <param name="value">A JSON string or null; configuration commands must never carry secrets.</param>
        /// <param name="optional">Whether rejection may allow dependents to start.</param>
        /// <returns>This descriptor for further declarations.</returns>
        /// <exception cref="ArgumentNullException">The descriptor, namespace, or key is null.</exception>
        /// <exception cref="ArgumentException">The namespace or key is blank, the key contains a slash, or the value is not a JSON string or null.</exception>
        /// <exception cref="InvalidOperationException">The descriptor is an immutable built snapshot.</exception>
        public IConfigurationStoreResourceDescriptor SetValue(string namespaceName, string key, JsonElement value, bool optional = false)
        {
            ArgumentNullException.ThrowIfNull(descriptor);
            ArgumentException.ThrowIfNullOrWhiteSpace(namespaceName);
            ArgumentException.ThrowIfNullOrWhiteSpace(key);
            if (key.Contains('/'))
            {
                throw new ArgumentException("Configuration keys must not contain '/' in declarative commands.", nameof(key));
            }
            if (value.ValueKind is not (JsonValueKind.String or JsonValueKind.Null))
            {
                throw new ArgumentException("Configuration values must be JSON strings or null.", nameof(value));
            }
            descriptor.AddCommand("configurationstore.set-value", $"{namespaceName}/{key}",
                new SetConfigurationValueCommandPayload(namespaceName, key, value),
                ConfigurationStoreCommandJsonContext.Default.SetConfigurationValueCommandPayload, optional);
            return descriptor;
        }

        /// <summary>Declares a string configuration value in a namespace.</summary>
        /// <param name="namespaceName">The namespace name.</param>
        /// <param name="key">The configuration key within the namespace, without slash characters.</param>
        /// <param name="value">The string value or null; configuration commands must never carry secrets.</param>
        /// <param name="optional">Whether rejection may allow dependents to start.</param>
        /// <returns>This descriptor for further declarations.</returns>
        /// <exception cref="ArgumentNullException">A required argument is null.</exception>
        /// <exception cref="ArgumentException">The namespace or key is blank, or the key contains a slash.</exception>
        /// <exception cref="InvalidOperationException">The descriptor is an immutable built snapshot.</exception>
        public IConfigurationStoreResourceDescriptor SetValue(string namespaceName, string key, string? value, bool optional = false)
        {
            return descriptor.SetValue(namespaceName, key,
                JsonSerializer.SerializeToElement(value, ConfigurationStoreCommandJsonContext.Default.String), optional);
        }

        /// <summary>Declares removal of a configuration value.</summary>
        /// <param name="namespaceName">The namespace name.</param>
        /// <param name="key">The configuration key within the namespace, without slash characters.</param>
        /// <param name="optional">Whether rejection may allow dependents to start.</param>
        /// <returns>This descriptor for further declarations.</returns>
        /// <exception cref="ArgumentNullException">The descriptor, namespace, or key is null.</exception>
        /// <exception cref="ArgumentException">The namespace or key is blank, or the key contains a slash.</exception>
        /// <exception cref="InvalidOperationException">The descriptor is an immutable built snapshot.</exception>
        public IConfigurationStoreResourceDescriptor RemoveValue(string namespaceName, string key, bool optional = false)
        {
            ArgumentNullException.ThrowIfNull(descriptor);
            ArgumentException.ThrowIfNullOrWhiteSpace(namespaceName);
            ArgumentException.ThrowIfNullOrWhiteSpace(key);
            if (key.Contains('/'))
            {
                throw new ArgumentException("Configuration keys must not contain '/' in declarative commands.", nameof(key));
            }
            descriptor.AddCommand("configurationstore.remove-value", $"{namespaceName}/{key}",
                new RemoveConfigurationValueCommandPayload(namespaceName, key),
                ConfigurationStoreCommandJsonContext.Default.RemoveConfigurationValueCommandPayload, optional);
            return descriptor;
        }
    }
}
