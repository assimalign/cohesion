namespace Assimalign.Cohesion.Hosting;

public enum HostState
{
    Idle = 0,
    Starting,
    Started,
    Running = Started,
    Stopping,
    Stopped,
}