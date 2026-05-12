using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Logging;

using Cohesion.Internal;

public static class LoggerExtensions
{


    public static void LogError(this ILogger logger, Exception exception)
    {
        //ThrowHelper.ThrowIfNull(logger).Log(new LoggerEntry()
        //{
        //    Level = LogLevel.Error,
            
        //});
    }

    public static void Trace(this ILogger logger)
    {

    }
}
