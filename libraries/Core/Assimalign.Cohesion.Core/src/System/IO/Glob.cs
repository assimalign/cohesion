using System;
using System.IO;
using System.Diagnostics;
using System.Globalization;

namespace System.IO;

using Assimalign.Cohesion.Internal;

/// <summary>
/// 
/// </summary>
[DebuggerDisplay("{ToString()}")]
public sealed partial class Glob 
{
    private static readonly Parser _parser = new Parser();

    private readonly TokenBase[] _tokens;

    internal Glob(TokenBase[] tokens)
    {
        _tokens = ThrowHelper.ThrowIfNullOrNone(tokens);
    }

    /// <summary>
    /// Gets the number of tokens in the glob.
    /// </summary>
    public int Count => Tokens.Length;

    /// <summary>
    /// 
    /// </summary>
    public Token[] Tokens => _tokens;


    #region Methods

    /// <summary>
    /// 
    /// </summary>
    /// <param name="path"></param>
    /// <returns></returns>
    public bool IsMatch(FileSystemPath path)
    {
        return IsMatch(path, CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// 
    /// </summary>
    /// <param name="path"></param>
    /// <param name="cultureInfo"></param>
    /// <returns></returns>
    public bool IsMatch(FileSystemPath path, CultureInfo cultureInfo)
    {
        return IsMatch(path, cultureInfo, false);
    }

    /// <summary>
    /// 
    /// </summary>
    /// <param name="path"></param>
    /// <param name="cultureInfo"></param>
    /// <param name="matchCase"></param>
    /// <returns></returns>
    public bool IsMatch(FileSystemPath path, CultureInfo cultureInfo, bool matchCase)
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
            if ((path.Length - position) != consumesMinLength)
            {
                // can't possibly match as tokens require a fixed length and the string length is different.
                return false;
            }
        }
        else if ((path.Length - position) < consumesMinLength)
        {
            // can't possibly match as tokens require a minimum length and the string is too short.
            return false;
        }

        var span = path.AsSpan();

        for (int i = 0; i < _tokens.Length; i++)
        {
            if (!_tokens[i].Test(span, cultureInfo, !matchCase, position, out position))
            {
                return false;
            }
        }

        // if all tokens matched but still more text then fail!
        if (position < path.Length - 1)
        {
            return false;
        }

        // Success.
        return true;
    }

    /// <summary>
    /// 
    /// </summary>
    /// <param name="pattern"></param>
    /// <returns></returns>
    public static Glob Parse(string pattern)
    {
        ThrowHelper.ThrowIfNullOrEmpty(pattern);

        var tokens = _parser.Tokenize(pattern);

        return new Glob(tokens);
    }

    #region Overloads

    public override string ToString()
    {
        return base.ToString();
    }

    public override bool Equals(object? obj)
    {
        return base.Equals(obj);
    }

    public override int GetHashCode()
    {
        return base.GetHashCode();
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
        Literal,
        CharacterSet,
        Any,
        Wildcard,
        WildcardDirectory,
        Range,
        PathSeparator,
    }

    /// <summary>
    /// 
    /// </summary>
    public abstract class Token
    {
        internal Token() { }

        /// <summary>
        /// 
        /// </summary>
        public abstract string Value { get; }

        /// <summary>
        /// 
        /// </summary>
        public abstract TokenKind Kind { get; }
    }

    #endregion
}