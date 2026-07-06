using System;

namespace Assimalign.Cohesion.Security.DataProtection;

/// <summary>
/// Convenience members for <see cref="IDataProtectionProvider"/>.
/// </summary>
public static class DataProtectionProviderExtensions
{
    extension(IDataProtectionProvider provider)
    {
        /// <summary>
        /// Creates a protector for a multi-segment purpose chain in one call. Equivalent to
        /// chaining <see cref="IDataProtectionProvider.CreateProtector(string)"/> for each
        /// segment in order, so
        /// <c>CreateProtector("a", "b")</c> derives the same subkey as
        /// <c>CreateProtector("a").CreateProtector("b")</c>.
        /// </summary>
        /// <param name="purposes">The ordered purpose segments; at least one is required.</param>
        /// <returns>A protector bound to the full purpose chain.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="provider"/> or <paramref name="purposes"/> is <see langword="null"/>.</exception>
        /// <exception cref="ArgumentException"><paramref name="purposes"/> is empty.</exception>
        public IDataProtector CreateProtector(params string[] purposes)
        {
            ArgumentNullException.ThrowIfNull(provider);
            ArgumentNullException.ThrowIfNull(purposes);
            if (purposes.Length == 0)
            {
                throw new ArgumentException("At least one purpose is required.", nameof(purposes));
            }

            IDataProtector protector = provider.CreateProtector(purposes[0]);
            for (int i = 1; i < purposes.Length; i++)
            {
                protector = protector.CreateProtector(purposes[i]);
            }

            return protector;
        }
    }
}
