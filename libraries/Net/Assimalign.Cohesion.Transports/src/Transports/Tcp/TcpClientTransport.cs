using System;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.Diagnostics;

namespace Assimalign.Cohesion.Transports;

using Assimalign.Cohesion.Transports.Internal;

[DebuggerDisplay("{Protocol} [{Kind}] - {_connections.Count}")]
public sealed class TcpClientTransport : ClientTransport<TcpTransportConnection>
{
    private readonly TcpClientTransportOptions _options;
    private readonly SocketTransportConnectionSettings _settings;
    private TransportPipeline _pipeline;
    private Socket? _socket;
    private bool _isDisposed;

    private readonly List<TcpTransportConnection> _connections;

    public TcpClientTransport(TcpClientTransportOptions? options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options;
        _settings = options.CreateConnectionSettings();
        _connections = new List<TcpTransportConnection>();
        _pipeline = options.BuildPipeline();
    }

    public override TransportProtocol Protocol { get; } = TransportProtocol.Tcp;

    /// <summary>
    /// The number of connections that are open.
    /// </summary>
    public IReadOnlyCollection<TcpTransportConnection> Connections => _connections.AsReadOnly();

    public override async Task<TcpTransportConnection> ConnectAsync(CancellationToken cancellationToken = default)
    {
        if (_socket is null)
        {
            _socket = _options.EndPoint switch
            {
                UnixDomainSocketEndPoint => new Socket(_options.EndPoint.AddressFamily, SocketType.Stream, System.Net.Sockets.ProtocolType.Unspecified),
                /* 
                    We're passing "ownsHandle: true" here even though we don't necessarily
                    own the handle because Socket.Dispose will clean-up everything safely.
                    If the handle was already closed or disposed then the socket will
                    be torn down gracefully, and if the caller never cleans up their handle
                    then we'll do it for them.

                    If we don't do this then we run the risk of Kestrel hanging because the
                    the underlying socket is never closed and the transport manager can hang
                    when it attempts to stop.
                */
                FileHandleEndPoint fileHandle => new Socket(new SafeSocketHandle((IntPtr)fileHandle.FileHandle, ownsHandle: true)),
                _ => new Socket(_options.EndPoint.AddressFamily, SocketType.Stream, System.Net.Sockets.ProtocolType.Tcp)
            };
            if (_options.EndPoint is IPEndPoint ip && ip.Address == IPAddress.IPv6Any)
            {
                _socket.DualMode = true;
            }
            if (_socket.LocalEndPoint is IPEndPoint)
            {
                _socket.NoDelay = _options.NoDelay;
            }
        }
        if (!_socket.Connected)
        {
            await _socket.ConnectAsync(_options.EndPoint);

            _settings.Socket = _socket;
        }
        while (true)
        {
            try
            {
                SocketTransportConnectionContext context = new SocketTransportConnectionContext(_settings)
                {
                    TransportId = Id
                };

                TcpTransportConnection connection = new TcpTransportConnection(context, _pipeline);

                _connections.Add(connection);

                context.OnDispose = () =>
                {
                    _connections.Remove(connection);
                };

                context.OnOpen = () =>
                {
                    _connections.Add(connection);
                };

                return connection;
            }
            catch (Exception)
            {
                _socket.Dispose();
                _isDisposed = true;
            }
        }
    }

    public override async ValueTask DisposeAsync()
    {
        if (_socket is not null && _socket.Connected)
        {
            _socket.Close();
        }
        if (_socket is not null && !_isDisposed)
        {
            _socket.Dispose();
        }

        foreach (TcpTransportConnection connection in _connections.ToArray())
        {
            await connection.DisposeAsync().ConfigureAwait(false);
        }

        _connections.Clear();
        _settings.PipeOptions.Dispose();
    }

    /// <summary>
    /// 
    /// </summary>
    /// <param name="configure"></param>
    /// <returns></returns>
    /// <exception cref="ArgumentNullException"></exception>
    public static TcpClientTransport Create(Action<TcpClientTransportOptions> configure)
    {
        var options = new TcpClientTransportOptions();

        ArgumentNullException.ThrowIfNull(configure);
        
        configure.Invoke(options);

        return new TcpClientTransport(options);
    }
}
