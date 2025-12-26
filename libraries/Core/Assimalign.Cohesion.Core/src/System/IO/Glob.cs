using System.Text;
using System.Diagnostics;
using System.Globalization;

namespace System.IO;

/// <summary>
/// The glob type is base implementation for utilizing wildcards to for single pattern match of strings values.
/// </summary>
[DebuggerDisplay("{ToString()}")]
public sealed partial class Glob 
{
    private static readonly Parser _parser = new Parser();

    private readonly TokenBase[] _tokens;

    internal Glob(TokenBase[] tokens)
    {
        _tokens = ArgumentNullException.ThrowIfNullOrNone(tokens);
    }

    /// <summary>
    /// Gets the number of tokens in the glob.
    /// </summary>
    public int Count => Tokens.Length;

    /// <summary>
    /// The parsed tokens
    /// </summary>
    public Token[] Tokens => _tokens;

    #region Methods

    /// <summary>
    /// 
    /// </summary>
    /// <param name="value"></param>
    /// <returns></returns>
    public bool IsMatch(ReadOnlySpan<char> value)
    {
        return IsMatch(value, CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// 
    /// </summary>
    /// <param name="value"></param>
    /// <param name="caseInSensitive"></param>
    /// <returns></returns>
    public bool IsMatch(ReadOnlySpan<char> value, bool caseInSensitive)
    {
        return IsMatch(value, CultureInfo.InvariantCulture, caseInSensitive);
    }

    /// <summary>
    /// 
    /// </summary>
    /// <param name="value"></param>
    /// <param name="cultureInfo"></param>
    /// <returns></returns>
    public bool IsMatch(ReadOnlySpan<char> value, CultureInfo cultureInfo)
    {
        return IsMatch(value, cultureInfo, false);
    }

    /// <summary>
    /// 
    /// </summary>
    /// <param name="value"></param>
    /// <param name="cultureInfo"></param>
    /// <param name="caseInSensitive"></param>
    /// <returns></returns>
    public bool IsMatch(ReadOnlySpan<char> value, CultureInfo cultureInfo, bool caseInSensitive)
    {
        bool consumesVariableLength = _tokens.Length == 0;
        int consumesMinLength = 0;
        int position = 0;

        for (int i = 0; i < _tokens.Length; i++)
        {
            consumesMinLength = consumesMinLength + _tokens[i].ConsumesMinLength;

            if (!consumesVariableLength)
            {
                if (_tokens[i].ConsumesVariableLength)
                {
                    consumesVariableLength = true;
                }
            }
        }

        if (!consumesVariableLength)
        {
            if ((value.Length - position) != consumesMinLength)
            {
                // can't possibly match as tokens require a fixed length and the string length is different.
                return false;
            }
        }
        else if ((value.Length - position) < consumesMinLength)
        {
            // can't possibly match as tokens require a minimum length and the string is too short.
            return false;
        }

        for (int i = 0; i < _tokens.Length; i++)
        {
            if (!_tokens[i].Test(value, cultureInfo, caseInSensitive, position, out position))
            {
                return false;
            }
        }

        // if all tokens matched but still more text then fail!
        if (position < value.Length - 1)
        {
            return false;
        }

        // Success.
        return true;
    }

    /// <summary>
    /// Returns a Glob object from the provided pattern.
    /// </summary>
    /// <param name="pattern">The glob pattern to parse.</param>
    /// <returns></returns>
    public static Glob Parse(string pattern)
    {
        ArgumentNullException.ThrowIfNullOrEmpty(pattern);

        TokenBase[] tokens = _parser.Tokenize(pattern);

        return new Glob(tokens);
    }

    #region Overloads

    public override string ToString()
    {
        var builder = new StringBuilder();

        for(int i = 0; i < _tokens.Length; i++)
        {
            Format(builder, _tokens[i]);
        }

        return builder.ToString();
    }

    private static StringBuilder Format(StringBuilder builder, TokenBase token)
    {
        if (token is CompositeGlobToken composite)
        {
            builder.Append(token.Value);

            for (int i = 0; i < composite.Tokens.Length; i++)
            {
                Format(builder, composite.Tokens[i]);
            }

            return builder;
        }

        return builder.Append(token.Value);
    }

    public override int GetHashCode()
    {
        return ToString().GetHashCode();
    }

    #endregion

    #endregion

    #region Operators

    /// <summary>
    /// 
    /// </summary>
    /// <param name="pattern"></param>
    public static implicit operator Glob(string pattern)
    {
        return Parse(pattern);
    }

    /// <summary>
    /// 
    /// </summary>
    /// <param name="glob"></param>

    public static implicit operator string(Glob glob)
    {
        return glob.ToString();
    }

    #endregion

    #region Partials

    /// <summary>
    /// 
    /// </summary>
    public enum TokenKind
    {
        /// <summary>
        /// 
        /// </summary>
        Literal,

        /// <summary>
        /// '[characters to check]' - 
        /// </summary>
        CharacterSet,

        /// <summary>
        /// '?' - 
        /// </summary>
        Any,

        /// <summary>
        /// '*' - 
        /// </summary>
        Wildcard,

        /// <summary>
        /// '**' - 
        /// </summary>
        WildcardDirectory,

        /// <summary>
        /// '[]'
        /// </summary>
        Range,

        /// <summary>
        /// '/' - 
        /// </summary>
        PathSeparator,

        // TODO - Support Brace Grouping
        /// <summary>
        /// '{*.cs, *.ts}'
        /// </summary>
        BraceGrouping
    }

    /// <summary>
    /// 
    /// </summary>
    public abstract class Token
    {
        internal Token() { }

        /// <summary>
        /// The raw token value.
        /// </summary>
        public abstract string Value { get; }

        /// <summary>
        /// The token kind.
        /// </summary>
        public abstract TokenKind Kind { get; }
    }

    #endregion
}