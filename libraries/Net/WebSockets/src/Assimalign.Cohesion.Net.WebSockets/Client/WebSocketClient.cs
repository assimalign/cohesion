using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Net.WebSockets;

using Assimalign.Cohesion.Net.Transports;
using System.Net;
using System.Net.WebSockets;

public sealed class WebSocketClient
{

    public WebSocketClient()
    {
        
    }


    public async Task ConnectAsync()
    {
        var transport = Transport.CreateTcpClient(options =>
        {
            options.Endpoint = new IPEndPoint(IPAddress.Loopback, 8085);
        });

        var connection = await transport.InitializeAsync();

        var stream = connection.Pipe.GetStream();

        var websocket = ClientWebSocket.CreateFromStream(stream, new WebSocketCreationOptions()
        {

        });
    }
}
