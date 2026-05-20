using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Database;

using Assimalign.Cohesion.Web;

public interface IDatabaseServer 
{
    int Version { get; }
}