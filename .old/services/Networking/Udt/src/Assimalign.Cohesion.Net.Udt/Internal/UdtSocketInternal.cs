using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Sockets;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UDTSOCKET = System.Int32;

namespace Assimalign.Cohesion.Net.Udt.Internal;

internal class UdtSocketInternal
{
    public UdtStatus m_Status;                       // current socket state

    public ulong m_TimeStamp;                     // time when the socket is closed

    public AddressFamily m_iIPversion;                         // IP version
    public IPEndPoint m_pSelfAddr;                    // pointer to the local address of the socket
    public IPEndPoint m_pPeerAddr;                    // pointer to the peer address of the socket

    public UDTSOCKET m_SocketID;                     // socket ID
    public UDTSOCKET m_ListenSocket;                 // ID of the listener socket; 0 means this is an independent socket

    public UDTSOCKET m_PeerID;                       // peer socket ID
    public int m_iISN;                           // initial sequence number, used to tell different connection from same IP:port

    public UdtCongestionControl m_pUDT;                             // pointer to the UDT entity

    public HashSet<UDTSOCKET> m_pQueuedSockets;    // set of connections waiting for accept()
    public HashSet<UDTSOCKET> m_pAcceptSockets;    // set of accept()ed connections

    public EventWaitHandle m_AcceptCond = new EventWaitHandle(false, EventResetMode.AutoReset);// used to block "accept" call
    public object m_AcceptLock = new object();             // mutex associated to m_AcceptCond

    public uint m_uiBackLog;                 // maximum number of connections in queue

    public int m_iMuxID;                             // multiplexer ID

    public object m_ControlLock = new object();            // lock this socket exclusively for control APIs: bind/listen/connect

    public UdtSocketInternal()
    {
        m_Status = UdtStatus.Initializing;
        m_iMuxID = -1;
    }

    public void Close()
    {
        m_AcceptCond.Close();
    }
}
