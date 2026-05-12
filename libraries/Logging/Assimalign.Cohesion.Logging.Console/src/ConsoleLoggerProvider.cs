
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Logging;

public class ConsoleLoggerProvider : ILoggerProvider
{
    public ConsoleLoggerProvider()
    {
        
    }

    public string Name { get; } = nameof(ConsoleLoggerProvider);
    public ILogger Create(string loggerName)
    {
        return default;
    }

    public void Dispose()
    {
        throw new NotImplementedException();
    }
}
