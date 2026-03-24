using System;

namespace Assimalign.Cohesion.Configuration;

public static partial class ConfigurationExtensions
{
    extension(IConfigurationBuilderContext context)
    {
        /// <summary>
        /// Attempts to read a typed property from the shared builder context.
        /// </summary>
        /// <typeparam name="T">The expected property value type.</typeparam>
        /// <param name="key">The property key.</param>
        /// <returns>The typed property value when present; otherwise the default value for <typeparamref name="T"/>.</returns>
        public T? GetProperty<T>(string key)
        {
            ArgumentNullException.ThrowIfNull(context);
            ArgumentException.ThrowIfNullOrEmpty(key);

            if (context is ConfigurationBuilderContext builderContext &&
                builderContext.TryGetProperty(key.AsSpan(), out T? typed))
            {
                return typed;
            }

            if (context.Properties.TryGetValue(key, out object? value) && value is T cast)
            {
                return cast;
            }

            return default;
        }
    }
}
