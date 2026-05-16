using System;
using System.Collections;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;

namespace System;

using Assimalign.Cohesion.Properties;

public static class ArgumentExceptionExtensions
{
    extension(ArgumentException exception)
    {
        public static void ThrowIf(
            [DoesNotReturnIf(true)] bool condition,
            [CallerArgumentExpression(nameof(condition))] string? message = null)
        {
            if (condition)
            {
                throw new ArgumentException(message);
            }
        }

        /// <summary>
        /// Throw an argument exception when the integral value does not exist in the given enum.
        /// </summary>
        /// <typeparam name="TStruct"></typeparam>
        /// <param name="argument"></param>
        /// <param name="paramName"></param>
        /// <returns></returns>
        /// <exception cref="ArgumentException"></exception>
        public static TEnum ThrowIfEnumNotDefined<TEnum>(
            [NotNull] TEnum argument,
            [CallerArgumentExpression(nameof(argument))] string? paramName = null)
            where TEnum : struct, Enum
        {
            if (!Enum.IsDefined(argument))
            {
                throw new ArgumentException(
                    string.Format(ErrorMessages.ArgumentExceptionOnEnumNotDefined, typeof(TEnum).FullName));
            }

            return argument;
        }

        /// <summary>
        /// Throws an argument exception when the argument is not of the specified type <paraamref="T"/>.
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <param name="argument">The argument to evaluate type.</param>
        /// <param name="paramName"></param>
        /// <returns></returns>
        /// <exception cref="ArgumentException"></exception>
        public static T ThrowIfNotOfType<T>(
            [NotNull] object argument,
            [CallerArgumentExpression(nameof(argument))] string? paramName = null)
        {
            if (argument is not T type)
            {
                throw new ArgumentException(
                    string.Format(ErrorMessages.ArgumentExceptionOnNotOfType, paramName, typeof(T).FullName));
            }

            return type;
        }
    }
}

public static class ArgumentNullExceptionExtensions
{
    extension(ArgumentNullException exception)
    {
        public static T ThrowIfNull<T>(
            [NotNull] T argument,
            [CallerArgumentExpression(nameof(argument))] string? paramName = null)
        {
            if (argument is null)
            {
                throw new ArgumentNullException(paramName);
            }
            return argument;
        }

        /// <summary>
        /// Throw an argument exception when the given span is empty.
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <param name="argument"></param>
        /// <param name="paramName"></param>
        /// <exception cref="ArgumentException"></exception>
        public static void ThrowIfEmptySpan<T>(
            [NotNull] ReadOnlySpan<T> argument,
            [CallerArgumentExpression(nameof(argument))] string? paramName = null)
        {
            if (argument.IsEmpty)
            {
                throw new ArgumentException(
                    string.Format(ErrorMessages.ArgumentExceptionOnEmptySpan, paramName));
            }
        }

        /// <summary>
        /// 
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <param name="argument"></param>
        /// <param name="paramName"></param>
        /// <returns></returns>
        /// <exception cref="ArgumentNullException"></exception>
        public static T ThrowIfNullOrNone<T>(
            [NotNull] T? argument,
            [CallerArgumentExpression(nameof(argument))] string? paramName = null) where T : IEnumerable
        {
            switch (argument)
            {
                case null:
                case ICollection collection when collection.Count == 0:
                case Array array when array.Length == 0:
                    throw new ArgumentNullException(paramName);
            }

            return argument;
        }
    }
}

public static class InvalidOperationExceptionExtensions
{
    extension(InvalidOperationException exception)
    {
        public static void ThrowIf(
            [DoesNotReturnIf(true)] bool condition,
            [CallerArgumentExpression(nameof(condition))] string? message = null)
        {
            if (condition)
            {
                throw new InvalidOperationException(message);
            }
        }
    }
}

public static class ArgumentOutOfRangeExceptionExtensions
{
    extension(ArgumentOutOfRangeException exception)
    {
        public static void ThrowIf(
            [DoesNotReturnIf(true)] bool condition,
            [CallerArgumentExpression(nameof(condition))] string? message = null)
        {
            if (condition)
            {
                throw new ArgumentOutOfRangeException(message);
            }
        }
    }
}