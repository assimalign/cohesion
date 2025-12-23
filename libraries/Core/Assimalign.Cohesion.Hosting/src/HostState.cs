namespace Assimalign.Cohesion.Hosting;

public enum HostState
{
    Unknown = 0,
    Starting,
    Started,
    Running = Started,
    Stopping,
    Stopped,
}
