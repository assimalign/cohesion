using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Net;
using System.Net.WebSockets;

namespace Assimalign.Cohesion.Net.WebSockets;

using Assimalign.Cohesion.Net.Transports;


public sealed class WebSocketClient
{

    public WebSocketClient()
    {
        
    }


    public async Task ConnectAsync()
    {
        var transport = Transport.CreateTcpClient(options =>
        {
            options.EndPoint = new IPEndPoint(IPAddress.Loopback, 8085);
        });

        var connection = await transport.InitializeAsync();

        var stream = connection.Pipe.GetStream();

        var websocket = ClientWebSocket.CreateFromStream(stream, new WebSocketCreationOptions()
        {

        });
    }
}
