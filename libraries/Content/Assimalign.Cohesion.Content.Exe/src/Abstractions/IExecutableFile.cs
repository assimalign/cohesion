using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

using Assimalign.Cohesion.Content.Binary;

namespace Assimalign.Cohesion.Content.Exe;

public interface IExecutableFile : IBinaryFile
{
    /// <summary>
    /// 
    /// </summary>
    /// <returns></returns>
    Task<ExecutableResult> InvokeAsync();
}
