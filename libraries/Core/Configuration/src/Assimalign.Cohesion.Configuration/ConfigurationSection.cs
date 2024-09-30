using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Configuration;

/// <summary>
/// Represents a section of application configuration values.
/// </summary>
public class ConfigurationSection : IConfigurationSection
{
    private readonly IConfigurationRoot root;
    private readonly string path;
    private string key;

    /// <summary>
    /// Initializes a new instance.
    /// </summary>
    /// <param name="root">The configuration root.</param>
    /// <param name="path">The path to this section.</param>
    public ConfigurationSection(IConfigurationRoot root, string path)
    {
        if (root == null)
        {
            throw new ArgumentNullException(nameof(root));
        }

        if (path == null)
        {
            throw new ArgumentNullException(nameof(path));
        }

        this.root = root;
        this.path = path;
    }

    /// <summary>
    /// Gets the full path to this section from the <see cref="IConfigurationRoot"/>.
    /// </summary>
    public string Path => path;

    /// <summary>
    /// Gets the key this section occupies in its parent.
    /// </summary>
    public string Key
    {
        get
        {
            if (key == null)
            {
                // Key is calculated lazily as last portion of Path
                key = ConfigurationPath.GetSectionKey(path);
            }
            return key;
        }
    }

    /// <summary>
    /// Gets or sets the section value.
    /// </summary>
    public string Value
    {
        get
        {
            return root[Path];
        }
        set
        {
            root[Path] = value;
        }
    }

    /// <summary>
    /// Gets or sets the value corresponding to a configuration key.
    /// </summary>
    /// <param name="key">The configuration key.</param>
    /// <returns>The configuration value.</returns>
    public string this[string key]
    {
        get
        {
            return root[ConfigurationPath.Combine(Path, key)];
        }

        set
        {
            root[ConfigurationPath.Combine(Path, key)] = value;
        }
    }

    /// <summary>
    /// Gets a configuration sub-section with the specified key.
    /// </summary>
    /// <param name="key">The key of the configuration section.</param>
    /// <returns>The <see cref="IConfigurationSection"/>.</returns>
    /// <remarks>
    ///     This method will never return <c>null</c>. If no matching sub-section is found with the specified key,
    ///     an empty <see cref="IConfigurationSection"/> will be returned.
    /// </remarks>
    public IConfigurationSection GetSection(string key) => root.GetSection(ConfigurationPath.Combine(Path, key));

    /// <summary>
    /// Gets the immediate descendant configuration sub-sections.
    /// </summary>
    /// <returns>The configuration sub-sections.</returns>
    public IEnumerable<IConfigurationSection> GetChildren() => root.GetChildrenImplementation(Path);

    /// <summary>
    /// Returns a <see cref="IChangeToken"/> that can be used to observe when this configuration is reloaded.
    /// </summary>
    /// <returns>The <see cref="IChangeToken"/>.</returns>
    public IChangeToken GetReloadToken() => root.GetReloadToken();
}
