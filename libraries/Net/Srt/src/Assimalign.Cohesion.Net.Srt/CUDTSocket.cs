using System;
using System.Net;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Net.Srt;

public interface ICUDTSocket
{
    /// <summary>
    /// This makes the socket no longer capable of performing any transmission
    /// operation, but continues to be responsive in the connection in order
    /// to finish sending the data that were scheduled for sending so far. 
    /// </summary>
    void SetClosed();
    /// <summary>
    /// This does the same as setClosed, plus sets the m_bBroken to true.
    /// Such a socket can still be read from so that remaining data from
    /// the receiver buffer can be read, but no longer sends anything.
    /// </summary>
    void SetBrokenClosed();
    void RemoveFromGrou(bool broken);
}
