using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Configuration;

namespace Assimalign.Cohesion.Web.Hosting.Tests.TestObjects;

/// <summary>
/// Builds an <see cref="IConfiguration"/> from flat <c>Section:Key</c> paths, the way the binder sees a
/// JSON or environment-variable source.
/// </summary>
internal static class SeededConfiguration
{
    /// <summary>
    /// Builds a configuration whose single provider holds <paramref name="values"/>.
    /// </summary>
    /// <param name="values">The values, keyed by configuration path.</param>
    /// <returns>The configuration.</returns>
    public static IConfiguration Build(IDictionary<string, string?> values)
    {
        ConfigurationManager manager = new();
        manager.AddProvider(new SeededConfigurationProvider(values));
        return manager;
    }

    private sealed class SeededConfigurationProvider : ConfigurationProvider
    {
        private readonly IDictionary<string, string?> _values;

        public SeededConfigurationProvider(IDictionary<string, string?> values)
        {
            _values = values;
        }

        public override string Name => "Seeded";

        protected override Task OnLoadAsync(IDictionary<Path, string?> entries, CancellationToken cancellationToken = default)
        {
            foreach (KeyValuePair<string, string?> value in _values)
            {
                entries[Path.Parse(value.Key)] = value.Value;
            }

            return Task.CompletedTask;
        }
    }
}
