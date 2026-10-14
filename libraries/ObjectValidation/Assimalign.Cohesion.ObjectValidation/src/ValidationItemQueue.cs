using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;

namespace Assimalign.Cohesion.ObjectValidation;

/// <summary>
/// The validation items of a profile, one per declared member, in a first-in, first-out queue: the
/// validator evaluates them in the order they were pushed, which is the order they are declared.
/// </summary>
/// <remarks>
/// Enumeration, the indexer, <see cref="ToArray()"/>, <see cref="CopyTo(IValidationItem[], int)"/>,
/// <c>Peek</c> and <c>Pop</c> all start at the item pushed first.
/// </remarks>
public class ValidationItemQueue : IValidationItemQueue
{
	private int _size;
	private int _version;
	private IValidationItem[] _array;



	public ValidationItemQueue()
	{
		_array = Array.Empty<IValidationItem>();
	}

	public ValidationItemQueue(int capacity)
	{
		if (capacity < 0)
		{
			throw new ArgumentOutOfRangeException("capacity", capacity, "Capacity must be greater than 0.");
		}
		this._array = new IValidationItem[capacity];
	}

	/// <summary>
	/// Initializes a queue holding the items of <paramref name="collection"/> in the collection's order, so
	/// its first item is evaluated first.
	/// </summary>
	/// <param name="collection">The items to queue.</param>
	/// <exception cref="ArgumentNullException"><paramref name="collection"/> is <see langword="null"/>.</exception>
	public ValidationItemQueue(IEnumerable<IValidationItem> collection)
	{
		if (collection == null)
		{
			throw new ArgumentNullException("collection");
		}
		this._array = ToArray(collection, out _size);
	}


	public int Count => _size;

    public IValidationItem this[int index] => _array[index];

    public void Clear()
	{
		if (RuntimeHelpers.IsReferenceOrContainsReferences<IValidationItem>())
		{
			Array.Clear(_array, 0, _size);
		}
		_size = 0;
		_version++;
	}

	public bool Contains(IValidationItem item)
	{
		if (_size != 0)
		{
			return Array.LastIndexOf(_array, item, _size - 1) != -1;
		}
		return false;
	}

	/// <summary>
	/// Copies the items to <paramref name="array"/>, starting at <paramref name="arrayIndex"/>, in queue order:
	/// the item pushed first is copied first.
	/// </summary>
	/// <param name="array">The array to copy to.</param>
	/// <param name="arrayIndex">The index in <paramref name="array"/> to copy the first item to.</param>
	/// <exception cref="ArgumentNullException"><paramref name="array"/> is <see langword="null"/>.</exception>
	/// <exception cref="ArgumentOutOfRangeException"><paramref name="arrayIndex"/> is negative or past the end of <paramref name="array"/>.</exception>
	/// <exception cref="ArgumentException"><paramref name="array"/> has too little room after <paramref name="arrayIndex"/>.</exception>
	public void CopyTo(IValidationItem[] array, int arrayIndex)
	{
		if (array == null)
		{
			throw new ArgumentNullException("array");
		}
		if (arrayIndex < 0 || arrayIndex > array.Length)
		{
			throw new ArgumentOutOfRangeException("arrayIndex", arrayIndex, "The index is either less than 0 or greater than the array.");
		}
		if (array.Length - arrayIndex < _size)
		{
			throw new ArgumentException("The size of the array is less than the current size.");
		}
		Array.Copy(this._array, 0, array, arrayIndex, _size);
	}


	public void TrimExcess()
	{
		int num = (int)((double)_array.Length * 0.9);
		if (_size < num)
		{
			Array.Resize(ref _array, _size);
			_version++;
		}
	}

	IValidationItem IValidationItemQueue.Peek()
	{
		if (_size == 0)
		{
			ThrowForEmptyQueue();
		}
		return _array[0];
	}

	bool IValidationItemQueue.TryPeek([MaybeNullWhen(false)] out IValidationItem result)
	{
		if (_size == 0)
		{
			result = default(IValidationItem);
			return false;
		}
		result = _array[0];
		return true;
	}


	IValidationItem IValidationItemQueue.Pop()
	{
		if (_size == 0)
		{
			ThrowForEmptyQueue();
		}
		return RemoveFront();
	}

	bool IValidationItemQueue.TryPop([MaybeNullWhen(false)] out IValidationItem result)
	{
		if (_size == 0)
		{
			result = default;
			return false;
		}
		result = RemoveFront();
		return true;
	}

	// Removes the item at the front, the one pushed first, and moves the rest up one place, so the items
	// stay at 0.._size - 1 in the order they were pushed: the order enumeration, the indexer and every copy
	// read.
	private IValidationItem RemoveFront()
	{
		IValidationItem result = _array[0];
		_size--;
		Array.Copy(_array, 1, _array, 0, _size);
		_array[_size] = default!;
		_version++;
		return result;
	}

	void IValidationItemQueue.Push(IValidationItem item)
	{
		int size = this._size;
		IValidationItem[] array = this._array;
		if ((uint)size < (uint)array.Length)
		{
			array[size] = item;
			_version++;
			this._size = size + 1;
		}
		else
		{
			PushWithResize(item);
		}
	}

	[MethodImpl(MethodImplOptions.NoInlining)]
	private void PushWithResize(IValidationItem item)
	{
		Grow(_size + 1);
		_array[_size] = item;
		_version++;
		_size++;
	}

	public int EnsureCapacity(int capacity)
	{
		if (capacity < 0)
		{
			throw new ArgumentOutOfRangeException("capacity", capacity, "Capacity must be greater than 0.");
		}
		if (_array.Length < capacity)
		{
			Grow(capacity);
			_version++;
		}
		return _array.Length;
	}

	private void Grow(int capacity)
	{
		int num = ((_array.Length == 0) ? 4 : (2 * _array.Length));
		if ((uint)num > 2147483591)
		{
			num = 2147483591;
		}
		if (num < capacity)
		{
			num = capacity;
		}
		Array.Resize(ref _array, num);
	}

	/// <summary>
	/// Copies the items to a new array in queue order: the item pushed first is at index 0.
	/// </summary>
	/// <returns>The items, in the order they are evaluated.</returns>
	public IValidationItem[] ToArray()
	{
		if (_size == 0)
		{
			return Array.Empty<IValidationItem>();
		}
		IValidationItem[] array = new IValidationItem[_size];
		Array.Copy(this._array, 0, array, 0, _size);
		return array;
	}

	private void ThrowForEmptyQueue()
	{
		throw new InvalidOperationException("The queue is empty.");
	}


	internal static T[] ToArray<T>(IEnumerable<T> source, out int length)
	{
		ICollection<T> collection = source as ICollection<T>;
		if (collection != null)
		{
			int count = collection.Count;
			if (count != 0)
			{
				T[] array = new T[count];
				collection.CopyTo(array, 0);
				length = count;
				return array;
			}
		}
		else
		{
			using IEnumerator<T> enumerator = source.GetEnumerator();
			if (enumerator.MoveNext())
			{
				T[] array2 = new T[4]
				{
					enumerator.Current,
					default(T),
					default(T),
					default(T)
				};
				int num = 1;
				while (enumerator.MoveNext())
				{
					if (num == array2.Length)
					{
						int num2 = num << 1;
						if ((uint)num2 > 2147483591)
						{
							num2 = ((2147483591 <= num) ? (num + 1) : 2147483591);
						}
						Array.Resize(ref array2, num2);
					}
					array2[num++] = enumerator.Current;
				}
				length = num;
				return array2;
			}
		}
		length = 0;
		return Array.Empty<T>();
	}

	/// <summary>
	/// Returns an enumerator over the items in queue order: the item pushed first, which is evaluated first,
	/// comes first.
	/// </summary>
	/// <returns>The enumerator.</returns>
	public IEnumerator<IValidationItem> GetEnumerator()
	{
		return new Enumerator(this);
	}

	IEnumerator IEnumerable.GetEnumerator()
	{
		return GetEnumerator();
	}

	internal struct Enumerator : IEnumerator<IValidationItem>, IDisposable, IEnumerator
	{
		private readonly int _version;
		private readonly ValidationItemQueue _queue;

		// -2 before the first MoveNext, -1 once the enumeration has ended, otherwise the current position
		// counted from the front of the queue.
		private int _index;
		private IValidationItem _current;

		public IValidationItem Current
		{
			get
			{
				if (_index < 0)
				{
					ThrowEnumerationNotStartedOrEnded();
				}
				return this._current;
			}
		}

		object? IEnumerator.Current => Current;

		internal Enumerator(ValidationItemQueue queue)
		{
			this._queue = queue;
			this._version = queue._version;
			this._index = -2;
			this._current = default;
		}

		public void Dispose()
		{
			this._index = -1;
		}

		public bool MoveNext()
		{
			if (this._version != _queue._version)
			{
				throw new InvalidOperationException("");// System.SR.InvalidOperation_EnumFailedVersion);
			}
			if (_index == -1)
			{
				return false;
			}
			_index = _index == -2 ? 0 : _index + 1;
			if (_index < _queue._size)
			{
				this._current = _queue._array[_index];
				return true;
			}
			_index = -1;
			this._current = default;
			return false;
		}

		private void ThrowEnumerationNotStartedOrEnded()
		{
			throw new InvalidOperationException("");// (_index == -2) ? System.SR.InvalidOperation_EnumNotStarted : System.SR.InvalidOperation_EnumEnded);
		}

		void IEnumerator.Reset()
		{
			if (_version != _queue._version)
			{
				throw new InvalidOperationException("");// System.SR.InvalidOperation_EnumFailedVersion);
			}
			_index = -2;
			_current = default;
		}
	}
}
