namespace Assimalign.Cohesion.Content.Exe;

/// <summary>How an invocation ended after all owned resources were released.</summary>
public enum ExecutableTerminationReason
{
    /// <summary>The process exited normally, with any exit code.</summary>
    Exited,
    /// <summary>The timeout expired; cleanup preceded return.</summary>
    TimedOut,
    /// <summary>The caller canceled; cleanup preceded return.</summary>
    Canceled,
    /// <summary>The operating system could not start the process.</summary>
    FailedToStart,
}
