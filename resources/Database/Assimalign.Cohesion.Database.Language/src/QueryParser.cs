using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Database.Language;

/// <summary>
/// Provides the common lexer, parsing, and analyzer flow for model-specific query parsers.
/// </summary>
public abstract class QueryParser
{
    private readonly QueryParserOptions _options;
    private readonly object? _gate;

    /// <summary>
    /// Initializes a query parser with its analyzer options.
    /// </summary>
    /// <param name="options">The analyzers and analyzer timeout applied after parsing.</param>
    /// <param name="serializeCalls">Whether calls on this parser serialize through analyzer completion.</param>
    /// <exception cref="ArgumentNullException"><paramref name="options"/> is null.</exception>
    protected QueryParser(QueryParserOptions options, bool serializeCalls = false)
    {
        ArgumentNullException.ThrowIfNull(options);
        _gate = serializeCalls ? new object() : null;
        _options = options;
    }

    /// <summary>The grammar surface this parser accepts.</summary>
    protected abstract QueryLanguageProfile Profile { get; }

    /// <summary>Whether the model this parser serves accepts <paramref name="clause"/>.</summary>
    /// <param name="clause">The language-specific clause name to inspect.</param>
    /// <returns><see langword="true"/> when the parser's profile accepts the clause.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="clause"/> is null.</exception>
    protected bool Supports(string clause) => Profile.Supports(clause);

    /// <summary>
    /// Records an unsupported-clause diagnostic on <paramref name="statement"/> when the model
    /// does not accept <paramref name="clause"/>. Returns whether the clause is accepted.
    /// </summary>
    /// <param name="statement">The statement on which to record an unsupported clause.</param>
    /// <param name="clause">The language-specific clause name to require.</param>
    /// <param name="location">The clause's source location.</param>
    /// <returns><see langword="true"/> when the parser's profile accepts the clause.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="statement"/> or <paramref name="clause"/> is null.</exception>
    protected bool RequireClause(QueryStatement statement, string clause, Location location)
    {
        ArgumentNullException.ThrowIfNull(statement);

        if (Supports(clause))
        {
            return true;
        }

        statement.AddDiagnostic(QueryDiagnostics.UnsupportedClause(clause, Profile.Language, location));
        return false;
    }

    /// <summary>
    /// Creates a lexer configured from this parser's language profile.
    /// </summary>
    /// <param name="query">The query source to tokenize.</param>
    /// <returns>A lexer positioned at the beginning of <paramref name="query"/>.</returns>
    protected TokenLexer CreateLexer(ReadOnlySpan<char> query) =>
        new(query, Profile.ToLexerOptions());

    /// <summary>
    /// Parses a query from its configured lexer.
    /// </summary>
    /// <param name="lexer">The lexer configured from <see cref="Profile"/>.</param>
    /// <param name="query">The source being parsed, for model-specific locations and retained text.</param>
    /// <returns>The parsed query statement.</returns>
    protected abstract QueryStatement ParseCore(TokenLexer lexer, ReadOnlySpan<char> query);

    /// <summary>Completes model-specific source retention after all analyzers succeed.</summary>
    /// <param name="statement">The parsed and analyzed statement.</param>
    /// <param name="query">The original query source.</param>
    protected abstract void CompleteParseCore(QueryStatement statement, ReadOnlySpan<char> query);

    /// <summary>
    /// Parses a query and runs the configured analyzers over its statement.
    /// </summary>
    /// <param name="query">The query source to parse.</param>
    /// <returns>The parsed and analyzed query statement.</returns>
    /// <exception cref="OperationCanceledException">An analyzer was canceled or the analyzer timeout expired.</exception>
    /// <remarks>Serialization follows the constructor policy and covers analyzer completion. Analyzer failures propagate to the caller. Analyzers must observe cancellation and must not depend on the caller's synchronization context.</remarks>
    public QueryStatement Parse(ReadOnlySpan<char> query)
    {
        if (_gate is null)
        {
            return ParseAndAnalyze(query);
        }

        lock (_gate)
        {
            return ParseAndAnalyze(query);
        }
    }

    private QueryStatement ParseAndAnalyze(ReadOnlySpan<char> query)
    {
        TokenLexer lexer = CreateLexer(query);
        QueryStatement statement = ParseCore(lexer, query);

        if (_options.Analyzers.Count != 0)
        {
            Analyze(new QueryAnalyzerContext(statement), _options.AnalyzerTimeout);
        }

        CompleteParseCore(statement, query);
        return statement;
    }

    private void Analyze(QueryAnalyzerContext context, TimeSpan timeout)
    {
        using var cancellationTokenSource = new CancellationTokenSource(timeout);
        cancellationTokenSource.Token.ThrowIfCancellationRequested();
        var analyzers = new List<Task>();

        try
        {
            foreach (var analyzer in _options.Analyzers)
            {
                analyzers.Add(analyzer.AnalyzeAsync(context, cancellationTokenSource.Token));
            }
        }
        catch
        {
            // Startup can fail synchronously after earlier analyzers returned tasks.
            // Observe their later faults while preserving the original startup exception.
            ObserveLateFaults(Task.WhenAll(analyzers));
            throw;
        }
        var completion = Task.WhenAll(analyzers);
        try
        {
            completion.WaitAsync(cancellationTokenSource.Token).GetAwaiter().GetResult();
        }
        catch (OperationCanceledException) when (cancellationTokenSource.IsCancellationRequested)
        {
            // An analyzer may ignore cancellation and fault after the caller has left.
            ObserveLateFaults(completion);
            throw;
        }
    }

    private static void ObserveLateFaults(Task completion)
    {
        _ = completion.ContinueWith(static task => { _ = task.Exception; }, CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }
}
