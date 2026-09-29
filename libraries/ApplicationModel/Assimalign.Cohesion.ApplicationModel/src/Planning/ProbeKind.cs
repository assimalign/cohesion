namespace Assimalign.Cohesion.ApplicationModel;

/// <summary>Identifies the mechanism used to evaluate a planned probe.</summary>
public enum ProbeKind
{
    /// <summary>An HTTP request evaluated against a container endpoint.</summary>
    Http = 0,

    /// <summary>A TCP connection attempt evaluated against a container endpoint.</summary>
    Tcp,

    /// <summary>A command executed inside the container.</summary>
    Exec,

    /// <summary>A gRPC health request evaluated against a container endpoint.</summary>
    Grpc,

    /// <summary>An explicitly disabled probe role retained from the manifest.</summary>
    None
}
