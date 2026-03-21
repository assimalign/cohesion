namespace Assimalign.Cohesion.Http.Transports.Internal.Http3;

internal enum Http3FrameType : long
{
    Data = 0x0,
    Headers = 0x1,
    Settings = 0x4
}
