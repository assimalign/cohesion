using System;
using System.Collections.Generic;
using System.Text;

namespace Assimalign.Cohesion.ApplicationModel;

/// <summary>
/// 
/// </summary>
public interface IApplicationModel
{
    /// <summary>
    /// 
    /// </summary>
    IApplicationResourceCollection Resources { get; }
}