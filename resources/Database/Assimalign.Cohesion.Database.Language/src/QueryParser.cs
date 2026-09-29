using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Database.Language;

/// <summary>
/// Provides the common lexer, parsing, and analyzer flow for model-specific query parsers.
/// </summary>
public abstract class QueryParser
{
    private readonly QueryParserOptions _options;

    /// <summary>
    /// Initializes a query parser with its analyzer options.
    /// </summary>
    /// <param name="options">The analyzers and analyzer timeout applied after parsing.</param>
    protected QueryParser(QueryParserOptions options)
    {
        _options = options;
    }

    /// <summary>The grammar surface this parser accepts.</summary>
    protected abstract QueryLanguageProfile Profile { get; }

    /// <summary>Whether the model this parser serves accepts <paramref name="clause"/>.</summary>
    /// <param name="clause">The language-specific clause name to inspect.</param>
    /// <returns><see langword="true"/> when the parser's profile accepts the clause.</returns>
    protected bool Supports(string clause) => Profile.Supports(clause);

    /// <summary>
    /// Records an unsupported-clause diagnostic on <paramref name="statement"/> when the model
    /// does not accept <paramref name="clause"/>. Returns whether the clause is accepted.
    /// </summary>
    /// <param name="statement">The statement on which to record an unsupported clause.</param>
    /// <param name="clause">The language-specific clause name to require.</param>
    /// <param name="location">The clause's source location.</param>
    /// <returns><see langword="true"/> when the parser's profile accepts the clause.</returns>
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
    protected virtual TokenLexer CreateLexer(ReadOnlySpan<char> query) =>
        new(query, Profile.ToLexerOptions());

    /// <summary>
    /// Parses a query from its configured lexer.
    /// </summary>
    /// <param name="lexer">The lexer configured from <see cref="Profile"/>.</param>
    /// <returns>The parsed query statement.</returns>
    protected abstract QueryStatement ParseCore(TokenLexer lexer);

    /// <summary>
    /// Parses a query and runs the configured analyzers over its statement.
    /// </summary>
    /// <param name="query">The query source to parse.</param>
    /// <returns>The parsed and analyzed query statement.</returns>
    public virtual QueryStatement Parse(ReadOnlySpan<char> query)
    {
        TokenLexer lexer = CreateLexer(query);

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
