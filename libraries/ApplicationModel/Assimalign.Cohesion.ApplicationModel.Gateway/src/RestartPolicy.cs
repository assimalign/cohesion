namespace Assimalign.Cohesion.ApplicationModel.Gateway;

/// <summary>Controls whether the local gateway restarts a supervised executable.</summary>
public enum RestartPolicy
{
    /// <summary>Restart after a non-zero exit or a liveness failure.</summary>
    OnFailure = 0,

    /// <summary>Restart after every exit or liveness failure.</summary>
    Always,

    /// <summary>Never restart the executable.</summary>
    Never
}
