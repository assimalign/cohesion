using System.Collections.Generic;
using System.Text;

namespace System.IO;

public sealed partial class Glob
{
    internal partial class Lexer : StringReader
    {
        private readonly string _text;
        private int _currentIndex;
        public const int FailedRead = -1;
        public const char NullChar = (char)0;

        public const char ExclamationMarkChar = '!';
        public const char StarChar = '*';
        public const char OpenBracketChar = '[';
        public const char CloseBracketChar = ']';
        public const char DashChar = '-';
        public const char QuestionMarkChar = '?';

        /// <summary>
        /// Tokens can start with the following characters.
        /// </summary>
        public static char[] BeginningOfTokenCharacters = { StarChar, OpenBracketChar, QuestionMarkChar };

        public static char[] AllowedNonAlphaNumericChars = { '.', ' ', '!', '#', '-', ';', '=', '@', '~', '_', ':' };

        /// <summary>
        /// The current delimiters
        /// </summary>
        private static readonly char[] PathSeparators = { '/', '\\' };

        public Lexer(string text) : base(text)
        {
            _text = text;
            _currentIndex = -1;
        }

        #region Properties

        public int CurrentIndex
        {
            get { return _currentIndex; }
            private set
            {
                _currentIndex = value;
                LastChar = _text[_currentIndex - 1];
                CurrentChar = _text[_currentIndex];
            }
        }
        public char LastChar { get; private set; }
        public char CurrentChar { get; private set; }
        public bool HasReachedEnd => base.Peek() == -1;
        public bool IsWhiteSpace => char.IsWhiteSpace(CurrentChar);
        public bool IsBeginningOfRangeOrList => CurrentChar == OpenBracketChar;
        public bool IsEndOfRangeOrList => CurrentChar == CloseBracketChar;
        public bool IsSingleCharacterMatch => CurrentChar == QuestionMarkChar;
        public bool IsWildcardCharacterMatch => CurrentChar == StarChar && Peek() != StarChar;
        public bool IsBeginningOfDirectoryWildcard => CurrentChar == StarChar && Peek() == StarChar;

        #endregion

        public bool TryRead()
        {
            return Read() != FailedRead;
        }
        public override int Read()
        {
            var result = base.Read();
            if (result != FailedRead)
            {
                _currentIndex++;
                LastChar = CurrentChar;
                CurrentChar = (char)result;
                return result;
            }

            return result;
        }
        public override int Read(char[] buffer, int index, int count)
        {
            var read = base.Read(buffer, index, count);
            CurrentIndex += read;
            CurrentChar = _text[CurrentIndex];
            return read;
        }
        public override int ReadBlock(char[] buffer, int index, int count)
        {
            var read = base.ReadBlock(buffer, index, count);
            CurrentIndex += read;
            return read;
        }
        public override string ReadLine()
        {
            var readLine = base.ReadLine();
            if (readLine != null)
            {
                CurrentIndex += readLine.Length;
            }
            return readLine!;
        }
        public string ReadPathSegment()
        {
            var segmentBuilder = new StringBuilder();
            while (TryRead())
            {
                if (!IsPathSeparator(CurrentChar))
                {
                    segmentBuilder.Append(CurrentChar);
                }
                else
                {
                    break;
                }
            }
            return segmentBuilder.ToString();
        }
        public override string ReadToEnd()
        {
            CurrentIndex = _text.Length - 1;
            return base.ReadToEnd();
        }
        public new char Peek()
        {
            if (HasReachedEnd)
            {
                return NullChar;
            }
            return (char)base.Peek();
        }
        public bool IsPathSeparator()
        {
            return IsPathSeparator(CurrentChar);
        }
        public static bool IsPathSeparator(char character)
        {

            var isCurrentCharacterStartOfDelimiter = character == PathSeparators[0] ||
                                                     character == PathSeparators[1];

            return isCurrentCharacterStartOfDelimiter;

        }
        public static bool IsNotStartOfToken(char character)
        {
            return !BeginningOfTokenCharacters.Contains(character);
        }
    }
    internal partial class Parser
    {
        private readonly StringBuilder _buffer;

        public Parser()
        {
            _buffer = new StringBuilder();
        }

        public TokenBase[] Tokenize(string pattern)
        {
            var tokens = new List<TokenBase>();

            using (var reader = new Lexer(pattern))
            {
                while (reader.TryRead())
                {
                    if (reader.IsBeginningOfRangeOrList)
                    {
                        tokens.Add(ParseRangeOrCharacterSet(reader));
                    }
                    else if (reader.IsSingleCharacterMatch)
                    {
                        tokens.Add(ParseSingleCharacterMatch());
                    }
                    else if (reader.IsWildcardCharacterMatch)
                    {
                        tokens.Add(ParseWildcard(reader));
                    }
                    else if (reader.IsPathSeparator())
                    {
                        var sepToken = ParsePathSeparator(reader);
                        tokens.Add(sepToken);
                    }
                    else if (reader.IsBeginningOfDirectoryWildcard)
                    {
                        if (tokens.Count > 0)
                        {
                            if (tokens[tokens.Count - 1] is PathSeparatorToken lastToken)
                            {
                                tokens.Remove(lastToken);
                                tokens.Add(ParseDirectoryWildcard(reader, lastToken));
                                continue;
                            }
                        }

                        tokens.Add(ParseDirectoryWildcard(reader, null!));
                    }
                    else
                    {
                        tokens.Add(ParseLiteral(reader));
                    }
                }
            }

            _buffer.Clear();

            return tokens.ToArray();
        }
        private TokenBase[] ParseComposite(Lexer reader)
        {
            var tokens = new List<TokenBase>();

            while (reader.TryRead())
            {
                if (reader.IsBeginningOfRangeOrList)
                {
                    tokens.Add(ParseRangeOrCharacterSet(reader));
                }
                else if (reader.IsSingleCharacterMatch)
                {
                    tokens.Add(ParseSingleCharacterMatch());
                }
                else if (reader.IsWildcardCharacterMatch)
                {
                    tokens.Add(ParseWildcard(reader));
                }
                else if (reader.IsPathSeparator())
                {
                    var sepToken = ParsePathSeparator(reader);
                    tokens.Add(sepToken);
                }
                else if (reader.IsBeginningOfDirectoryWildcard)
                {
                    if (tokens.Count > 0)
                    {
                        if (tokens[tokens.Count - 1] is PathSeparatorToken lastToken)
                        {
                            tokens.Remove(lastToken);
                            tokens.Add(ParseDirectoryWildcard(reader, lastToken));
                            continue;
                        }
                    }

                    tokens.Add(ParseDirectoryWildcard(reader, null!));
                }
                else
                {
                    tokens.Add(ParseLiteral(reader));
                }
            }

            return tokens.ToArray();
        }
        private TokenBase ParseDirectoryWildcard(Lexer reader, PathSeparatorToken leadingPathSeparatorToken)
        {
            reader.TryRead();

            if (Lexer.IsPathSeparator(reader.Peek()))
            {
                reader.TryRead();
                var trailingSeparator = ParsePathSeparator(reader);

                return new WildcardDirectoryToken(
                    leadingPathSeparatorToken,
                    (PathSeparatorToken)trailingSeparator,
                    ParseComposite(reader));
            }

            return new WildcardDirectoryToken(
                leadingPathSeparatorToken,
                null!,
                ParseComposite(reader)); // this shouldn't happen unless a pattern ends with ** which is weird. **sometext is not legal.
        }
        private TokenBase ParseLiteral(Lexer reader)
        {
            AcceptCurrentChar(reader);

            while (!reader.HasReachedEnd)
            {
                var peek = reader.Peek();
                var isValid = Lexer.IsNotStartOfToken(peek) && !Lexer.IsPathSeparator(peek);

                if (isValid)
                {
                    if (reader.TryRead())
                    {
                        AcceptCurrentChar(reader);
                    }
                    else
                    {
                        // potentially hit end of string.
                        break;
                    }
                }
                else
                {
                    // we have hit a character that may not be a valid literal (could be unsupported, or start of a token for instance).
                    break;
                }
            }

            return new LiteralToken(GetBufferAndReset());
        }
        private TokenBase ParseRangeOrCharacterSet(Lexer reader) // Parses a token for a range or list globbing expression.
        {
            bool isNegated = false;
            bool isNumberRange = false;
            bool isLetterRange = false;
            bool isCharList = false;

            if (reader.Peek() == Lexer.ExclamationMarkChar)
            {
                isNegated = true;
                reader.Read();
            }

            var nextChar = reader.Peek();
            if (Char.IsLetterOrDigit(nextChar))
            {
                reader.Read();
                nextChar = reader.Peek();
                if (nextChar == Lexer.DashChar)
                {
                    if (Char.IsLetter(reader.CurrentChar))
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

                AcceptCurrentChar(reader);
            }
            else
            {
                isCharList = true;
                reader.Read();
                AcceptCurrentChar(reader);
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
                    if (peekChar == Lexer.CloseBracketChar)
                    {
                        AcceptCurrentChar(reader);
                    }
                    else
                    {
                        break;
                    }
                }
                else
                {
                    AcceptCurrentChar(reader);
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
        private TokenBase ParsePathSeparator(Lexer reader)
        {
            return new PathSeparatorToken();
        }
        private TokenBase ParseWildcard(Lexer reader)
        {
            var children = ParseComposite(reader);

            return new WildcardToken(children);
        }
        private TokenBase ParseSingleCharacterMatch()
        {
            return new AnyCharacterToken();
        }
        private void AcceptCurrentChar(Lexer reader)
        {
            //if (reader.CurrentChar == '\\')
            //{
            //    _buffer.Append('/'); // Normalize any backslashes to forward slashes
            //}
            //else
            //{
                _buffer.Append(reader.CurrentChar);
            //}
        }
        private string GetBufferAndReset()
        {
            var text = _buffer.ToString();

            _buffer.Clear();
            return text;
        }

        //private void AcceptChar(char character)
        //{
        //    _buffer.Append(character);
        //}
    }
}
