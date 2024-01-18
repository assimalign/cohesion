using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Net.Hosting.Tests;

public class TestServer : IHostServer
{
    private readonly Timer timer;

    public TestServerState State { get; } = new();
    IHostServerState IHostServer.State => this.State;

    public TestServer()
    {
        this.timer = new Timer(
            new TimerCallback(WriteRunning), "The server is running", TimeSpan.Zero, TimeSpan.FromSeconds(5));
    }



    public async ValueTask StartAsync(CancellationToken cancellationToken = default)
    {
        var timelimit = DateTime.Now.AddMinutes(5);
        
        State.Status = HostServerStatus.Running;

        while (true)
        {
            if (DateTime.Now >= timelimit)
            {
                break;
            }
        }
    }

    public void WriteRunning(object message)
    {
        Console.WriteLine(message);
    }

    public async ValueTask StopAsync(CancellationToken cancellationToken = default)
    {
        
    }
}
