using System;

namespace Assimalign.Cohesion.Transports;

/// <summary>
/// Represents an error caused by an invalid transport pipeline configuration.
/// </summary>
public class TransportPipelineConfigurationException : TransportException
{
    /// <summary>
    /// Initializes a new instance of the <see cref="TransportPipelineConfigurationException"/> class.
    /// </summary>
    /// <param name="message">The message that describes the configuration error.</param>
    public TransportPipelineConfigurationException(string message)
        : base(message) { }

    /// <summary>
    /// Initializes a new instance of the <see cref="TransportPipelineConfigurationException"/> class.
    /// </summary>
    /// <param name="message">The message that describes the configuration error.</param>
    /// <param name="inner">The exception that caused the current exception.</param>
    public TransportPipelineConfigurationException(string message, Exception inner)
        : base(message, inner) { }
}
