using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Net.Dns;

/// <summary>
/// 
/// </summary>
public abstract class DnsRecord
{
    
    protected DnsRecord()
    {
        
    }
    /// <summary>
    /// 
    /// </summary>
    public abstract DnsRecordKind Kind { get; }
    /// <summary>
    /// 
    /// </summary>
    public int TimeToLive { get; }
}
