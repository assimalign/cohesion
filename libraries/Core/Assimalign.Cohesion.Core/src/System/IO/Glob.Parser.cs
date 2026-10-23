using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection.PortableExecutable;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using static System.IO.Glob;

using Assimalign.Cohesion;

namespace System.IO;

public sealed partial class Glob
{

    [global::System.Diagnostics.DebuggerDisplay("Lexer")]
    [global::System.Diagnostics.DebuggerTypeProxy(typeof(DebugView))]
    internal ref struct Lexer
    {
        private static ReadOnlySpan<char> _separators => new char[] { '/', '\\' };
        private static ReadOnlySpan<char> _starting => new char[] { Star, OpenBracket, QuestionMark, OpenBrace };

        private readonly ReadOnlySpan<char> _pattern;
        private int _index;


        public Lexer(ReadOnlySpan<char> pattern)
        {
            _pattern = pattern;
            _index = -1;
        }


        public const char ExclamationMark = '!';
        public const char Star = '*';
        public const char OpenBracket = '[';
        public const char CloseBracket = ']';
        public const char OpenBrace = '{';
        public const char CloseBrace = '}';
        public const char Dash = '-';
        public const char QuestionMark = '?';


        public ref readonly char Current
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get { return ref _pattern[_index]; }
        }

        public int Index
        {
            get { return _index; }
        }

        public bool IsSeparator
        {
            get { return (Current == _separators[0] || Current == _separators[1]); }
        }

        // [ ]
        public bool IsStartOfRangeOrList
        {
            get { return Current == OpenBracket; }
        }

        //
        public bool IsEndOfRangeOrList
        {
            get { return Current == CloseBracket; }
        }

        //
        public bool IsBeginningOfBraceGrouping
        {
            get { return Current == OpenBrace; }
        }
        public bool IsEndOfBraceGrouping
        {
            get { return Current == CloseBrace; }
        }
        public bool IsSingleCharacterMatch
        {
            get { return Current == QuestionMark; }
        }
        public bool IsWildcardCharacterMatch
        {
            get { return Current == Star && Peek() != Star; }
        }
        // **
        public bool IsStartOfDirectoryWildcard
        {
            get { return Current == Star && Peek() == Star; }
        }

        public bool End
        {
            get
            {
                int pos = _index + 1;
                if (pos > _pattern.Length)
                {
                    return true;
                }

                return false;
            }
        }


        public int Read()
        {
            if (TryRead())
            {
                return 1;
            }

            return -1;
        }

        public bool TryRead()
        {
            int num = _index + 1;
            if (num < _pattern.Length)
            {
                _index = num;
                return true;
            }
            return false;
        }

        public char Peek()
        {
            if (TryPeek(out char next))
            {
                return next;
            }

            return (char)0;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool TryPeek(out char c)
        {
            int num = _index + 1;
            if (num < _pattern.Length)
            {
                c = _pattern[num];
                return true;
            }
            c = default(char);
            return false;
        }


        public static bool IsPathSeparator(char character)
        {
            return character == _separators[0] || character == _separators[1];
        }
        public static bool IsNotStartOfToken(char character)
        {
            return !_starting.Contains(character);
        }

        private sealed class DebugView
        {
            private readonly int _member0;
            private readonly bool _member1;

            public DebugView(Lexer value)
            {
                _member0 = value.Index;
                _member1 = value.End;
            }

            public int Index => _member0;
            public bool End => _member1;
        }
    }

    [global::System.Diagnostics.DebuggerDisplay("Parser")]
    [global::System.Diagnostics.DebuggerTypeProxy(typeof(DebugView))]
    internal partial class Parser
    {
        // The cache starts over after this many misses. Patterns can be caller input or paths (the
        // FileSystem providers' Watch parses each watched path), so a process-wide cache must not
        // keep every distinct pattern; clearing keeps the recent working set without bookkeeping
        // on each hit.
        private const uint cacheCapacity = 1024;

        // Lookups are lock-free and allocation-free, so a cached Glob.Parse allocates only the Glob.
        private static readonly ConcurrentDictionary<string, GlobPattern> _cache = new(StringComparer.Ordinal);
        private static uint _cacheMisses;

        // Per-parse state: Tokenize creates a parser for each miss, so concurrent parses never
        // share this buffer.
        private readonly StringBuilder _buffer;

        private Parser()
        {
            _buffer = new StringBuilder();
        }

        public static GlobPattern Tokenize(string pattern)
        {
            if (_cache.TryGetValue(pattern, out GlobPattern? cached))
            {
                return cached;
            }

            TokenBase[] tokens = new Parser().Parse(pattern);

            // Unsigned wraparound keeps the period: 2^32 is a multiple of the capacity.
            if (Interlocked.Increment(ref _cacheMisses) % cacheCapacity == 0)
            {
                _cache.Clear();
            }

            // When first parses of one pattern race, every caller gets the array that was stored.
            return _cache.GetOrAdd(pattern, new GlobPattern(tokens));
        }

        private TokenBase[] Parse(string pattern)
        {
            List<TokenBase> tokens = new List<TokenBase>();
            ReadOnlySpan<char> span = pattern.AsSpan();
            Lexer lexer = new Lexer(span);

            while (lexer.TryRead())
            {
                if (lexer.IsStartOfRangeOrList)
                {
                    tokens.Add(ParseRangeOrCharacterSet(ref lexer));
                }
                else if (lexer.IsBeginningOfBraceGrouping)
                {
                    tokens.Add(ParseBraceGrouping(ref lexer));
                }
                else if (lexer.IsSingleCharacterMatch)
                {
                    tokens.Add(ParseSingleCharacterMatch());
                }
                else if (lexer.IsWildcardCharacterMatch)
                {
                    tokens.Add(ParseWildcard(ref lexer));
                }
                else if (lexer.IsSeparator)
                {
                    var sepToken = ParsePathSeparator(ref lexer);
                    tokens.Add(sepToken);
                }
                else if (lexer.IsStartOfDirectoryWildcard)
                {
                    if (tokens.Count > 0)
                    {
                        if (tokens[tokens.Count - 1] is PathSeparatorToken lastToken)
                        {
                            tokens.Remove(lastToken);
                            tokens.Add(ParseDirectoryWildcard(ref lexer, lastToken));
                            continue;
                        }
                    }

                    tokens.Add(ParseDirectoryWildcard(ref lexer, null!));
                }
                else
                {
                    tokens.Add(ParseLiteral(ref lexer));
                }
            }

            return tokens.ToArray();
        }
        private TokenBase[] ParseComposite(ref Lexer reader)
        {
            var tokens = new List<TokenBase>();

            while (reader.TryRead())
            {
                if (reader.IsStartOfRangeOrList)
                {
                    tokens.Add(ParseRangeOrCharacterSet(ref reader));
                }
                else if (reader.IsBeginningOfBraceGrouping)
                {
                    tokens.Add(ParseBraceGrouping(ref reader));
                }
                else if (reader.IsSingleCharacterMatch)
                {
                    tokens.Add(ParseSingleCharacterMatch());
                }
                else if (reader.IsWildcardCharacterMatch)
                {
                    tokens.Add(ParseWildcard(ref reader));
                }
                else if (reader.IsSeparator)
                {
                    var sepToken = ParsePathSeparator(ref reader);
                    tokens.Add(sepToken);
                }
                else if (reader.IsStartOfDirectoryWildcard)
                {
                    if (tokens.Count > 0)
                    {
                        if (tokens[tokens.Count - 1] is PathSeparatorToken lastToken)
                        {
                            tokens.Remove(lastToken);
                            tokens.Add(ParseDirectoryWildcard(ref reader, lastToken));
                            continue;
                        }
                    }

                    tokens.Add(ParseDirectoryWildcard(ref reader, null!));
                }
                else
                {
                    tokens.Add(ParseLiteral(ref reader));
                }
            }

            return tokens.ToArray();
        }
        private TokenBase ParseDirectoryWildcard(ref Lexer reader, PathSeparatorToken leadingPathSeparatorToken)
        {
            reader.TryRead();

            if (Lexer.IsPathSeparator(reader.Peek()))
            {
                reader.TryRead();
                var trailingSeparator = ParsePathSeparator(ref reader);

                return new WildcardDirectoryToken(
                    leadingPathSeparatorToken,
                    (PathSeparatorToken)trailingSeparator,
                    ParseComposite(ref reader));
            }

            return new WildcardDirectoryToken(
                leadingPathSeparatorToken,
                null!,
                ParseComposite(ref reader)); // this shouldn't happen unless a pattern ends with ** which is weird. **sometext is not legal.
        }
        private TokenBase ParseLiteral(ref Lexer reader)
        {
            _buffer.Append(reader.Current);

            while (!reader.End)
            {
                char peek = reader.Peek();
                bool isValid = Lexer.IsNotStartOfToken(peek) && !Lexer.IsPathSeparator(peek);

                if (!isValid)
                {
                    // we have hit a character that may not be a valid literal (could be unsupported, or start of a token for instance).
                    break;
                }

                if (!reader.TryRead())
                {
                    // potentially hit end of string.
                    break;
                }
                _buffer.Append(reader.Current);
            }

            return new LiteralToken(GetBufferAndReset());
        }
        private TokenBase ParseRangeOrCharacterSet(ref Lexer reader) // Parses a token for a range or list globbing expression.
        {
            bool isNegated = false;
            bool isNumberRange = false; // example: [0-1]   = match any number within the given range
            bool isLetterRange = false; // example: [A-b]   = match any character within the given range
            bool isCharList = false;    // example: [Abrt]  = match one of the characters

            // Check if the range is negated
            if (reader.Peek() == Lexer.ExclamationMark)
            {
                isNegated = true;
                reader.Read();
            }

            var nextChar = reader.Peek();
            if (Char.IsLetterOrDigit(nextChar))
            {
                reader.Read();
                nextChar = reader.Peek();
                if (nextChar == Lexer.Dash)
                {
                    if (Char.IsLetter(reader.Current))
                    {
                        isLetterRange = true;
                    }
                    else
                    {
                        isNumberRange = true;
                    }
                }
                else
                {
                    isCharList = true;
                }

                _buffer.Append(reader.Current);
            }
            else
            {
                isCharList = true;
                reader.Read();
                _buffer.Append(reader.Current);
            }

            if (isLetterRange || isNumberRange)
            {
                // skip over the dash char
                reader.TryRead();
            }

            while (reader.TryRead())
            {
                if (reader.IsEndOfRangeOrList)
                {
                    var peekChar = reader.Peek();
                    // Close brackets within brackets are escaped with another
                    // Close bracket. e.g. [a]] matches a[
                    if (peekChar == Lexer.CloseBracket)
                    {
                        _buffer.Append(reader.Current);
                    }
                    else
                    {
                        break;
                    }
                }
                else
                {
                    _buffer.Append(reader.Current);
                }
            }

            // construct token
            TokenBase result = null!;
            var value = GetBufferAndReset();
            if (isCharList)
            {
                result = new CharacterSetToken(value.ToCharArray(), isNegated);
            }
            else if (isLetterRange)
            {
                var start = value[0];
                var end = value[1];
                result = new RangeToken(start, end, isNegated);
            }
            else if (isNumberRange)
            {
                var start = value[0]; // int.Parse(value[0].ToString());
                var end = value[1]; // int.Parse(value[1].ToString());
                result = new RangeToken(start, end, isNegated);
            }

            return result;
        }
        private TokenBase ParseBraceGrouping(ref Lexer reader)
        {
            //List<CompositeGlobToken> alternatives = new List<CompositeGlobToken>();
            List<TokenBase> alternatives = new List<TokenBase>();
            int start = reader.Index;

            while (reader.TryRead())
            {
                if (reader.IsEndOfBraceGrouping)
                {
                    //// Add the last alternative before closing
                    //if (currentAlternative.Count > 0 || alternatives.Count > 0)
                    //{
                    //    alternatives.Add(new BraceGroupingToken(currentAlternative.ToArray()));
                    //}
                    break;
                }
                else if (reader.Current == ',')
                {
                    continue;
                    //// Comma separates alternatives
                    //alternatives.Add(new BraceGroupingToken(currentAlternative.ToArray()));
                    //currentAlternative = new List<TokenBase>();
                }
                else if (reader.IsStartOfRangeOrList)
                {
                    alternatives.Add(ParseRangeOrCharacterSet(ref reader));
                }
                else if (reader.IsSingleCharacterMatch)
                {
                    alternatives.Add(ParseSingleCharacterMatch());
                }
                else if (reader.IsWildcardCharacterMatch)
                {
                    alternatives.Add(ParseWildcard(ref reader));
                }
                else if (reader.IsSeparator)
                {
                    alternatives.Add(ParsePathSeparator(ref reader));
                }
                else
                {
                    alternatives.Add(ParseLiteralInBraceGroup(ref reader));
                }
            }

            // Empty alternatives are dropped, so {} and {,} have none, nor does a { that ends the
            // pattern. Nothing could match such a group.
            if (alternatives.Count == 0)
            {
                throw new ArgumentException(
                    string.Format(CultureInfo.CurrentCulture, ErrorMessages.GlobBraceGroupEmpty, start),
                    "pattern");
            }

            return new BraceGroupingToken(alternatives.ToArray());
        }

        private TokenBase ParseLiteralInBraceGroup(ref Lexer reader)
        {
            _buffer.Append(reader.Current);

            while (!reader.End)
            {
                char peek = reader.Peek();
                // Stop at comma, closing brace, or any start token, or separator
                bool isValid = Lexer.IsNotStartOfToken(peek) && 
                               !Lexer.IsPathSeparator(peek) && 
                               peek != ',' && 
                               peek != Lexer.CloseBrace;

                if (!isValid)
                {
                    break;
                }

                if (!reader.TryRead())
                {
                    break;
                }
                _buffer.Append(reader.Current);
            }

            return new LiteralToken(GetBufferAndReset());
        }
        private TokenBase ParsePathSeparator(ref Lexer reader)
        {
            return new PathSeparatorToken();
        }
        private TokenBase ParseWildcard(ref Lexer reader)
        {
            var children = ParseComposite(ref reader);

            return new WildcardToken(children);
        }
        private TokenBase ParseSingleCharacterMatch()
        {
            return new AnyCharacterToken();
        }


        private string GetBufferAndReset()
        {
            var text = _buffer.ToString();

            _buffer.Clear();
            return text;
        }

        private sealed class DebugView
        {
            public DebugView(Parser value)
            {
            }
        }
    }
}
