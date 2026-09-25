using System;

namespace Assimalign.Cohesion.ApplicationModel.Gateway;

/// <summary>Describes the telemetry destination and credential carrier prepared for one resource.</summary>
public interface IResourceTelemetry
{
    /// <summary>Gets the absolute OTLP HTTP ingestion endpoint.</summary>
    Uri Endpoint { get; }

    /// <summary>Gets the sensitive HTTP headers document to place in the resource's protected file carrier.</summary>
    /// <remarks>The controller must not log this content or place it in environment variables.</remarks>
    ReadOnlyMemory<byte> HeadersDocument { get; }
}
