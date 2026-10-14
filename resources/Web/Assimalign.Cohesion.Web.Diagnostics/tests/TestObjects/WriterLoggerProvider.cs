using System;

using Assimalign.Cohesion.Logging;
using Assimalign.Cohesion.Web.Diagnostics.Internal;

namespace Assimalign.Cohesion.Web.Diagnostics.Tests.TestObjects;

internal sealed class WriterLoggerProvider : LoggerProvider
{
    private readonly W3CAccessLogWriter _writer;

    public WriterLoggerProvider(W3CAccessLogWriter writer)
    {
        _writer = writer;
    }

    public override string Name => "FailingAccessLogWriter";

    protected override Logger CreateCore(string category) => new WriterLogger(category, _writer);

    private sealed class WriterLogger : Logger
    {
        private readonly W3CAccessLogWriter _writer;

        public WriterLogger(string category, W3CAccessLogWriter writer)
            : base(category)
        {
            _writer = writer;
        }

        protected override void WriteCore(ILoggerEntry entry) => _writer.Write(entry);

        protected override IScopedLogger BeginScopeCore(ILoggerEntry entry) => throw new NotSupportedException();
    }
}
