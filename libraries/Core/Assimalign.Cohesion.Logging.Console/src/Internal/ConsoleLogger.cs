using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Logging.Internal;

using Cohesion.Internal;

public class ConsoleLogger : ILogger
{
    public IScopeLogger BeginScope(ILoggerEntry entry)
    {


        throw new NotImplementedException();
    }

    public void Log(ILoggerEntry entry)
    {
        ThrowHelper.ThrowIfNull(entry);

    }


    partial class ScopeConsoleLogger : IScopeLogger
    {
        public ScopeConsoleLogger(LogId parentId)
        {
            ParentId = parentId;
        }

        public LogId ParentId { get; }

        public IScopeLogger BeginScope(ILoggerEntry entry)
        {
            throw new NotImplementedException();
        }

        public void Dispose()
        {
            throw new NotImplementedException();
        }

        public void Log(ILoggerEntry entry)
        {
            throw new NotImplementedException();
        }
    }
}
