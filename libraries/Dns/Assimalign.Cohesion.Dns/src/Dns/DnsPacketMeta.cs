using System;
using System.IO;
using System.Text.Json;

namespace Assimalign.Cohesion.Dns;

public class DnsPacketMeta
{
    #region Variables

    readonly DnsNsAddress _server;
    readonly int _size;
    readonly double _rtt;

    #endregion

    #region Constructors

    public DnsPacketMeta(DnsNsAddress server, int size, double rtt)
    {
        _server = server;
        _size = size;
        _rtt = rtt;

        if (_rtt < 0.1)
            _rtt = 0.1;
    }

    public DnsPacketMeta(BinaryReader bR)
    {
        byte version = bR.ReadByte();
        switch (version)
        {
            case 1:
                _server = new DnsNsAddress(bR);
                _size = bR.ReadInt32();
                _rtt = bR.ReadDouble();
                break;

            default:
                throw new InvalidDataException("DnsDatagramMetadata format version not supported.");
        }
    }

    #endregion

    #region public

    public void WriteTo(BinaryWriter bW)
    {
        bW.Write((byte)1); //version

        _server.WriteTo(bW);
        bW.Write(_size);
        bW.Write(_rtt);
    }

    public void SerializeTo(Utf8JsonWriter jsonWriter)
    {
        jsonWriter.WriteStartObject();

        jsonWriter.WriteString("NameServer", _server?.ToString());
        jsonWriter.WriteString("Protocol", (_server is null ? DnsTransportProtocol.Udp : _server.Protocol).ToString());
        jsonWriter.WriteString("DatagramSize", _size + " bytes");
        jsonWriter.WriteString("RoundTripTime", Math.Round(_rtt, 2) + " ms");

        jsonWriter.WriteEndObject();
    }

    #endregion

    #region properties

    public DnsNsAddress NameServer
    { get { return _server; } }

    public DnsTransportProtocol Protocol
    { get { return _server is null ? DnsTransportProtocol.Udp : _server.Protocol; } }

    public int DatagramSize
    { get { return _size; } }

    public double RoundTripTime
    { get { return _rtt; } }

    #endregion
}
