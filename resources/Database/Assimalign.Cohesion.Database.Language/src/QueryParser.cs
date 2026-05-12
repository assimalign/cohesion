using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection.Metadata;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Database.Language;

public abstract class QueryParser
{
    private readonly QueryParserOptions _options;

    protected QueryParser(QueryParserOptions options)
    {
        _options = options;
    }


    protected abstract TokenLexerOptions Options { get; }

    protected virtual TokenLexer CreateLexer(ReadOnlySpan<char> query, TokenLexerOptions options)
    {
        return new TokenLexer(query, options);
    }

    protected abstract QueryStatement ParseCore(TokenLexer lexer);


    public virtual QueryStatement Parse(ReadOnlySpan<char> query)
    {

        TokenLexer lexer = CreateLexer(query, Options);

        QueryStatement statement = ParseCore(lexer);

        Analyze(new QueryAnalyzerContext(statement), _options.AnalyzerTimeout);

        return statement;
    }






    private void Analyze(QueryAnalyzerContext context, TimeSpan timeout)
    {
        using var cancellationTokenSource = new CancellationTokenSource(timeout); // Max 10 seconds for analysis
#if !DEBUG
        cancellationTokenSource.Token.ThrowIfCancellationRequested();
#endif
        var analyzers = new List<Task>();

        foreach (var analyzer in _options.Analyzers)
        {
            analyzers.Add(analyzer.AnalyzeAsync(context, cancellationTokenSource.Token));
        }
        while (analyzers.Any())
        {
            var task = Task.WhenAny(analyzers);

            while (!task.IsCompleted)
            {
                if (cancellationTokenSource.IsCancellationRequested)
                {
                    throw new OperationCanceledException(cancellationTokenSource.Token);
                }
            }

            analyzers.Remove(task.Result);
        }
    }
}
