﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Hosting;

/// <summary>
/// 
/// </summary>
public interface IHostServerState
{
    /// <summary>
    /// 
    /// </summary>
    HostServerStatus Status { get; }
}
