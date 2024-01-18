using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Net.Logging;

public interface ILogger
{

    /// <summary>
    /// 
    /// </summary>
    /// <param name="level"></param>
    /// <param name="message"></param>
    void Log(LogLevel level, string message);

    /// <summary>
    /// 
    /// </summary>
    /// <returns></returns>
    ILoggerBatch CreateLogBatch();
}
