using System;
using System.Collections.Generic;

using Assimalign.Cohesion.Content.Binary;
using Assimalign.Cohesion.Content.Bmff.Internal;

namespace Assimalign.Cohesion.Content.Bmff;

/// <summary>Typed and FourCC queries over ordered BMFF and profile trees.</summary>
/// <remarks>Queries are lazy, use depth-first preorder and do not read payloads. Composite queries
/// exclude the receiver; document descendants include top-level boxes. Do not mutate during enumeration.
/// Each direct child is at depth zero for the query's depth limit.</remarks>
public static partial class BmffQueryExtensions
{
    extension(BmffBoxComposite box)
    {
        /// <summary>Enumerates direct children assignable to T.</summary>
        /// <typeparam name="T">The requested box type, including profile types.</typeparam>
        /// <returns>The matching boxes in tree order.</returns>
        /// <exception cref="ArgumentNullException">The receiver is null.</exception>
        public IEnumerable<T> Children<T>() where T : BmffBox
        {
            ArgumentNullException.ThrowIfNull(box);
            return ChildrenCore<T>(box.Children);
        }

        /// <summary>Enumerates descendants assignable to T in depth-first order.</summary>
        /// <typeparam name="T">The requested box type, including profile types.</typeparam>
        /// <param name="maxDepth">Maximum inclusive descendant depth; defaults to 64.</param>
        /// <returns>The matching boxes in tree order.</returns>
        /// <exception cref="ArgumentNullException">The receiver is null.</exception>
        /// <exception cref="ArgumentOutOfRangeException">The depth limit is negative.</exception>
        /// <exception cref="ContentFormatException">A visited node is null, cyclic or too deep.</exception>
        public IEnumerable<T> Descendants<T>(int maxDepth = 64) where T : BmffBox
        {
            ArgumentNullException.ThrowIfNull(box);
            ArgumentOutOfRangeException.ThrowIfNegative(maxDepth);
            return DescendantsCore<T>(box.Children, maxDepth);
        }

        /// <summary>Finds the first descendant assignable to T.</summary>
        /// <typeparam name="T">The requested box type, including profile types.</typeparam>
        /// <param name="maxDepth">Maximum inclusive descendant depth; defaults to 64.</param>
        /// <returns>The first matching box.</returns>
        /// <exception cref="ArgumentNullException">The receiver is null.</exception>
        /// <exception cref="ArgumentOutOfRangeException">The depth limit is negative.</exception>
        /// <exception cref="ContentFormatException">A visited node is null, cyclic or too deep.</exception>
        /// <exception cref="InvalidOperationException">No box matches.</exception>
        public T First<T>(int maxDepth = 64) where T : BmffBox
        {
            ArgumentNullException.ThrowIfNull(box);
            ArgumentOutOfRangeException.ThrowIfNegative(maxDepth);
            return Required(box.Descendants<T>(maxDepth));
        }

        /// <summary>Finds the first descendant assignable to T, or null.</summary>
        /// <typeparam name="T">The requested box type, including profile types.</typeparam>
        /// <param name="maxDepth">Maximum inclusive descendant depth; defaults to 64.</param>
        /// <returns>The first matching box, or null.</returns>
        /// <exception cref="ArgumentNullException">The receiver is null.</exception>
        /// <exception cref="ArgumentOutOfRangeException">The depth limit is negative.</exception>
        /// <exception cref="ContentFormatException">A visited node is null, cyclic or too deep.</exception>
        public T? FirstOrDefault<T>(int maxDepth = 64) where T : BmffBox
        {
            ArgumentNullException.ThrowIfNull(box);
            ArgumentOutOfRangeException.ThrowIfNegative(maxDepth);
            return Optional(box.Descendants<T>(maxDepth));
        }

        /// <summary>Enumerates all descendants with the raw type.</summary>
        /// <param name="type">The raw type, including unregistered values.</param>
        /// <param name="maxDepth">Maximum inclusive descendant depth; defaults to 64.</param>
        /// <returns>The matching boxes in tree order.</returns>
        /// <exception cref="ArgumentNullException">The receiver is null.</exception>
        /// <exception cref="ArgumentOutOfRangeException">The depth limit is negative.</exception>
        /// <exception cref="ContentFormatException">A visited node is null, cyclic or too deep.</exception>
        public IEnumerable<BmffBox> FindAll(BmffBoxType type, int maxDepth = 64)
        {
            ArgumentNullException.ThrowIfNull(box);
            ArgumentOutOfRangeException.ThrowIfNegative(maxDepth);
            return FindCore(box.Children, type, maxDepth);
        }

        /// <summary>Finds the first descendant with the raw type.</summary>
        /// <param name="type">The raw type, including unregistered values.</param>
        /// <param name="maxDepth">Maximum inclusive descendant depth; defaults to 64.</param>
        /// <returns>The first matching box.</returns>
        /// <exception cref="ArgumentNullException">The receiver is null.</exception>
        /// <exception cref="ArgumentOutOfRangeException">The depth limit is negative.</exception>
        /// <exception cref="ContentFormatException">A visited node is null, cyclic or too deep.</exception>
        /// <exception cref="InvalidOperationException">No box matches.</exception>
        public BmffBox First(BmffBoxType type, int maxDepth = 64)
        {
            ArgumentNullException.ThrowIfNull(box);
            ArgumentOutOfRangeException.ThrowIfNegative(maxDepth);
            return Required(box.FindAll(type, maxDepth));
        }

        /// <summary>Finds the first descendant with the raw type, or null.</summary>
        /// <param name="type">The raw type, including unregistered values.</param>
        /// <param name="maxDepth">Maximum inclusive descendant depth; defaults to 64.</param>
        /// <returns>The first matching box, or null.</returns>
        /// <exception cref="ArgumentNullException">The receiver is null.</exception>
        /// <exception cref="ArgumentOutOfRangeException">The depth limit is negative.</exception>
        /// <exception cref="ContentFormatException">A visited node is null, cyclic or too deep.</exception>
        public BmffBox? FirstOrDefault(BmffBoxType type, int maxDepth = 64)
        {
            ArgumentNullException.ThrowIfNull(box);
            ArgumentOutOfRangeException.ThrowIfNegative(maxDepth);
            return Optional(box.FindAll(type, maxDepth));
        }

        /// <summary>Enumerates all descendants with the raw type.</summary>
        /// <param name="type">The raw type, including unregistered values.</param>
        /// <param name="maxDepth">Maximum inclusive descendant depth; defaults to 64.</param>
        /// <returns>The matching boxes in tree order.</returns>
        /// <exception cref="ArgumentNullException">The receiver is null.</exception>
        /// <exception cref="ArgumentOutOfRangeException">The depth limit is negative.</exception>
        /// <exception cref="ContentFormatException">A visited node is null, cyclic or too deep.</exception>
        public IEnumerable<BmffBox> FindAll(FourCC type, int maxDepth = 64)
        {
            ArgumentNullException.ThrowIfNull(box);
            ArgumentOutOfRangeException.ThrowIfNegative(maxDepth);
            return FindCore(box.Children, type.ToBmffBoxType(), maxDepth);
        }

        /// <summary>Finds the first descendant with the raw type.</summary>
        /// <param name="type">The raw type, including unregistered values.</param>
        /// <param name="maxDepth">Maximum inclusive descendant depth; defaults to 64.</param>
        /// <returns>The first matching box.</returns>
        /// <exception cref="ArgumentNullException">The receiver is null.</exception>
        /// <exception cref="ArgumentOutOfRangeException">The depth limit is negative.</exception>
        /// <exception cref="ContentFormatException">A visited node is null, cyclic or too deep.</exception>
        /// <exception cref="InvalidOperationException">No box matches.</exception>
        public BmffBox First(FourCC type, int maxDepth = 64)
        {
            ArgumentNullException.ThrowIfNull(box);
            ArgumentOutOfRangeException.ThrowIfNegative(maxDepth);
            return Required(box.FindAll(type, maxDepth));
        }

        /// <summary>Finds the first descendant with the raw type, or null.</summary>
        /// <param name="type">The raw type, including unregistered values.</param>
        /// <param name="maxDepth">Maximum inclusive descendant depth; defaults to 64.</param>
        /// <returns>The first matching box, or null.</returns>
        /// <exception cref="ArgumentNullException">The receiver is null.</exception>
        /// <exception cref="ArgumentOutOfRangeException">The depth limit is negative.</exception>
        /// <exception cref="ContentFormatException">A visited node is null, cyclic or too deep.</exception>
        public BmffBox? FirstOrDefault(FourCC type, int maxDepth = 64)
        {
            ArgumentNullException.ThrowIfNull(box);
            ArgumentOutOfRangeException.ThrowIfNegative(maxDepth);
            return Optional(box.FindAll(type, maxDepth));
        }

    }

    extension(BmffDocument document)
    {
        /// <summary>Enumerates direct children assignable to T.</summary>
        /// <typeparam name="T">The requested box type, including profile types.</typeparam>
        /// <returns>The matching boxes in tree order.</returns>
        /// <exception cref="ArgumentNullException">The receiver is null.</exception>
        public IEnumerable<T> Children<T>() where T : BmffBox
        {
            ArgumentNullException.ThrowIfNull(document);
            return ChildrenCore<T>(document.Boxes);
        }

        /// <summary>Enumerates descendants assignable to T in depth-first order.</summary>
        /// <typeparam name="T">The requested box type, including profile types.</typeparam>
        /// <param name="maxDepth">Maximum inclusive descendant depth; defaults to 64.</param>
        /// <returns>The matching boxes in tree order.</returns>
        /// <exception cref="ArgumentNullException">The receiver is null.</exception>
        /// <exception cref="ArgumentOutOfRangeException">The depth limit is negative.</exception>
        /// <exception cref="ContentFormatException">A visited node is null, cyclic or too deep.</exception>
        public IEnumerable<T> Descendants<T>(int maxDepth = 64) where T : BmffBox
        {
            ArgumentNullException.ThrowIfNull(document);
            ArgumentOutOfRangeException.ThrowIfNegative(maxDepth);
            return DescendantsCore<T>(document.Boxes, maxDepth);
        }

        /// <summary>Finds the first descendant assignable to T.</summary>
        /// <typeparam name="T">The requested box type, including profile types.</typeparam>
        /// <param name="maxDepth">Maximum inclusive descendant depth; defaults to 64.</param>
        /// <returns>The first matching box.</returns>
        /// <exception cref="ArgumentNullException">The receiver is null.</exception>
        /// <exception cref="ArgumentOutOfRangeException">The depth limit is negative.</exception>
        /// <exception cref="ContentFormatException">A visited node is null, cyclic or too deep.</exception>
        /// <exception cref="InvalidOperationException">No box matches.</exception>
        public T First<T>(int maxDepth = 64) where T : BmffBox
        {
            ArgumentNullException.ThrowIfNull(document);
            ArgumentOutOfRangeException.ThrowIfNegative(maxDepth);
            return Required(document.Descendants<T>(maxDepth));
        }

        /// <summary>Finds the first descendant assignable to T, or null.</summary>
        /// <typeparam name="T">The requested box type, including profile types.</typeparam>
        /// <param name="maxDepth">Maximum inclusive descendant depth; defaults to 64.</param>
        /// <returns>The first matching box, or null.</returns>
        /// <exception cref="ArgumentNullException">The receiver is null.</exception>
        /// <exception cref="ArgumentOutOfRangeException">The depth limit is negative.</exception>
        /// <exception cref="ContentFormatException">A visited node is null, cyclic or too deep.</exception>
        public T? FirstOrDefault<T>(int maxDepth = 64) where T : BmffBox
        {
            ArgumentNullException.ThrowIfNull(document);
            ArgumentOutOfRangeException.ThrowIfNegative(maxDepth);
            return Optional(document.Descendants<T>(maxDepth));
        }

        /// <summary>Enumerates all descendants with the raw type.</summary>
        /// <param name="type">The raw type, including unregistered values.</param>
        /// <param name="maxDepth">Maximum inclusive descendant depth; defaults to 64.</param>
        /// <returns>The matching boxes in tree order.</returns>
        /// <exception cref="ArgumentNullException">The receiver is null.</exception>
        /// <exception cref="ArgumentOutOfRangeException">The depth limit is negative.</exception>
        /// <exception cref="ContentFormatException">A visited node is null, cyclic or too deep.</exception>
        public IEnumerable<BmffBox> FindAll(BmffBoxType type, int maxDepth = 64)
        {
            ArgumentNullException.ThrowIfNull(document);
            ArgumentOutOfRangeException.ThrowIfNegative(maxDepth);
            return FindCore(document.Boxes, type, maxDepth);
        }

        /// <summary>Finds the first descendant with the raw type.</summary>
        /// <param name="type">The raw type, including unregistered values.</param>
        /// <param name="maxDepth">Maximum inclusive descendant depth; defaults to 64.</param>
        /// <returns>The first matching box.</returns>
        /// <exception cref="ArgumentNullException">The receiver is null.</exception>
        /// <exception cref="ArgumentOutOfRangeException">The depth limit is negative.</exception>
        /// <exception cref="ContentFormatException">A visited node is null, cyclic or too deep.</exception>
        /// <exception cref="InvalidOperationException">No box matches.</exception>
        public BmffBox First(BmffBoxType type, int maxDepth = 64)
        {
            ArgumentNullException.ThrowIfNull(document);
            ArgumentOutOfRangeException.ThrowIfNegative(maxDepth);
            return Required(document.FindAll(type, maxDepth));
        }

        /// <summary>Finds the first descendant with the raw type, or null.</summary>
        /// <param name="type">The raw type, including unregistered values.</param>
        /// <param name="maxDepth">Maximum inclusive descendant depth; defaults to 64.</param>
        /// <returns>The first matching box, or null.</returns>
        /// <exception cref="ArgumentNullException">The receiver is null.</exception>
        /// <exception cref="ArgumentOutOfRangeException">The depth limit is negative.</exception>
        /// <exception cref="ContentFormatException">A visited node is null, cyclic or too deep.</exception>
        public BmffBox? FirstOrDefault(BmffBoxType type, int maxDepth = 64)
        {
            ArgumentNullException.ThrowIfNull(document);
            ArgumentOutOfRangeException.ThrowIfNegative(maxDepth);
            return Optional(document.FindAll(type, maxDepth));
        }

        /// <summary>Enumerates all descendants with the raw type.</summary>
        /// <param name="type">The raw type, including unregistered values.</param>
        /// <param name="maxDepth">Maximum inclusive descendant depth; defaults to 64.</param>
        /// <returns>The matching boxes in tree order.</returns>
        /// <exception cref="ArgumentNullException">The receiver is null.</exception>
        /// <exception cref="ArgumentOutOfRangeException">The depth limit is negative.</exception>
        /// <exception cref="ContentFormatException">A visited node is null, cyclic or too deep.</exception>
        public IEnumerable<BmffBox> FindAll(FourCC type, int maxDepth = 64)
        {
            ArgumentNullException.ThrowIfNull(document);
            ArgumentOutOfRangeException.ThrowIfNegative(maxDepth);
            return FindCore(document.Boxes, type.ToBmffBoxType(), maxDepth);
        }

        /// <summary>Finds the first descendant with the raw type.</summary>
        /// <param name="type">The raw type, including unregistered values.</param>
        /// <param name="maxDepth">Maximum inclusive descendant depth; defaults to 64.</param>
        /// <returns>The first matching box.</returns>
        /// <exception cref="ArgumentNullException">The receiver is null.</exception>
        /// <exception cref="ArgumentOutOfRangeException">The depth limit is negative.</exception>
        /// <exception cref="ContentFormatException">A visited node is null, cyclic or too deep.</exception>
        /// <exception cref="InvalidOperationException">No box matches.</exception>
        public BmffBox First(FourCC type, int maxDepth = 64)
        {
            ArgumentNullException.ThrowIfNull(document);
            ArgumentOutOfRangeException.ThrowIfNegative(maxDepth);
            return Required(document.FindAll(type, maxDepth));
        }

        /// <summary>Finds the first descendant with the raw type, or null.</summary>
        /// <param name="type">The raw type, including unregistered values.</param>
        /// <param name="maxDepth">Maximum inclusive descendant depth; defaults to 64.</param>
        /// <returns>The first matching box, or null.</returns>
        /// <exception cref="ArgumentNullException">The receiver is null.</exception>
        /// <exception cref="ArgumentOutOfRangeException">The depth limit is negative.</exception>
        /// <exception cref="ContentFormatException">A visited node is null, cyclic or too deep.</exception>
        public BmffBox? FirstOrDefault(FourCC type, int maxDepth = 64)
        {
            ArgumentNullException.ThrowIfNull(document);
            ArgumentOutOfRangeException.ThrowIfNegative(maxDepth);
            return Optional(document.FindAll(type, maxDepth));
        }

    }

    private static IEnumerable<T> ChildrenCore<T>(IList<BmffBox> children) where T : BmffBox
    {
        for (var index = 0; index < children.Count; index++)
        {
            if (children[index] is T match) { yield return match; }
        }
    }

    private static IEnumerable<T> DescendantsCore<T>(IList<BmffBox> roots, int maxDepth) where T : BmffBox
    {
        var cursor = new BmffTraversal(roots, maxDepth);
        while (cursor.MoveNext())
        {
            if (!cursor.IsLeaving && cursor.Box is T match) { yield return match; }
        }
    }

    private static IEnumerable<BmffBox> FindCore(IList<BmffBox> roots, BmffBoxType type, int maxDepth)
    {
        foreach (var box in DescendantsCore<BmffBox>(roots, maxDepth))
        {
            if (box.BoxType == type) { yield return box; }
        }
    }

    private static T Required<T>(IEnumerable<T> boxes) where T : BmffBox =>
        Optional(boxes) ?? throw new InvalidOperationException(ErrorMessages.BoxNotFound);

    private static T? Optional<T>(IEnumerable<T> boxes) where T : BmffBox
    {
        using var enumerator = boxes.GetEnumerator();
        return enumerator.MoveNext() ? enumerator.Current : null;
    }
}
