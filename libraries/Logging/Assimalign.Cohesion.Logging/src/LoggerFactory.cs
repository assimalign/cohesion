using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Logging;

using Cohesion.Internal;
using Cohesion.Logging.Internal;

public class LoggerFactory : ILoggerFactory
{
    private readonly Lock _lock;
    private readonly ConcurrentDictionary<string, ILogger> _factory;
    private readonly ConcurrentDictionary<string, ILogger>.AlternateLookup<ReadOnlySpan<char>> _lookup;
    private readonly List<ILoggerProvider> _providers;

    public LoggerFactory(IEnumerable<ILoggerProvider> providers)
    {
        _providers = providers.ToList();
        _factory = new ConcurrentDictionary<string, ILogger>(StringComparer.OrdinalIgnoreCase);
        _lookup = _factory.GetAlternateLookup<ReadOnlySpan<char>>();
        _lock = new Lock();
    }

    public IEnumerable<ILoggerProvider> Providers => _providers;

    public ILogger Create(string loggerName)
    {
        ArgumentNullException.ThrowIfNullOrEmpty(loggerName);

        var lookupName = loggerName.AsSpan();

        if (!_lookup.TryGetValue(lookupName, out ILogger? logger)) 
        {
            lock (_lock)
            {
                var loggers = new ILogger[_providers.Count];

                for (int i = 0; i < _providers.Count; i++)
                {
                    loggers[i] = _providers[i].Create(loggerName);

                    logger = new CompositeLogger(loggers);

                    _lookup[lookupName] = logger;
                }
            }
        }

        return logger!;
    }

    public void Dispose()
    {
        throw new NotImplementedException();
    }
}
