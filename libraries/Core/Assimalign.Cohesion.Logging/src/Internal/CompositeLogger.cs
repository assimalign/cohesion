using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Logging.Internal;

internal sealed class CompositeLogger : ILogger
{
    public CompositeLogger(ILogger[] loggers)
    {
        
    }

    public void Log(ILoggerEntry entry)
    {
        throw new NotImplementedException();
    }

    public IScopedLogger BeginScope(ILoggerEntry entry)
    {
        throw new NotImplementedException();
    }

    public bool IsEnabled(LogLevel level)
    {
        throw new NotImplementedException();
    }
}
