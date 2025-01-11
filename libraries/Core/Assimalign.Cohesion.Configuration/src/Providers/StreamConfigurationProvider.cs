using System;
using System.IO;


namespace Assimalign.Cohesion.Configuration;

/// <summary>
/// Stream based configuration provider
/// </summary>
public abstract class StreamConfigurationProvider : ConfigurationProviderOld
{
    private bool isLoaded;

    /// <summary>
    /// Constructor.
    /// </summary>
    /// <param name="source">The source.</param>
    public StreamConfigurationProvider(StreamConfigurationSource source)
    {
        Source = source ?? throw new ArgumentNullException(nameof(source));
    }

    /// <summary>
    /// The source settings for this provider.
    /// </summary>
    public StreamConfigurationSource Source { get; }

    /// <summary>
    /// Load the configuration data from the stream.
    /// </summary>
    /// <param name="stream">The data stream.</param>
    public abstract void Load(Stream stream);

    /// <summary>
    /// Load the configuration data from the stream. Will throw after the first call.
    /// </summary>
    public override void Load()
    {
        if (isLoaded)
        {
            throw new InvalidOperationException("The current Configuration Stream Provider instance is already loaded.");
        }
        Load(Source.Stream);
        isLoaded = true;
    }
}
