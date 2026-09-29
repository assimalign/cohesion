using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;

namespace Assimalign.Cohesion.ObjectValidation;

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
		int num = 0;
		int num2 = arrayIndex + _size;
		while (num < _size)
		{
			array[--num2] = this._array[num++];
		}
	}

	//void ICollection<IValidationItem>.CopyTo(Array array, int arrayIndex)
	//{
	//	if (array == null)
	//	{
	//		throw new ArgumentNullException("array");
	//	}
	//	if (array.Rank != 1)
	//	{
	//		throw new ArgumentException("Multi-dimension arrays not supported.", "array");
	//	}
	//	if (array.GetLowerBound(0) != 0)
	//	{
	//		throw new ArgumentException("Non-zero lower-bound arrays no supported.", "array");
	//	}
	//	if (arrayIndex < 0 || arrayIndex > array.Length)
	//	{
	//		throw new ArgumentOutOfRangeException("arrayIndex", arrayIndex, "The index is either less than 0 or greater than the array.");
	//	}
	//	if (array.Length - arrayIndex < size)
	//	{
	//		throw new ArgumentException("The size of the array is less than the current size.");
	//	}
	//	try
	//	{
	//		Array.Copy(this.array, 0, array, arrayIndex, size);
	//		Array.Reverse(array, arrayIndex, size);
	//	}
	//	catch (ArrayTypeMismatchException)
	//	{
	//		throw new ArgumentException("The array type is invalid", "array");
	//	}
	//}


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
		int num = _size - 1;
		IValidationItem[] array = this._array;
		if ((uint)num >= (uint)array.Length)
		{
			ThrowForEmptyStack();
		}
		return array[num];
	}

	bool IValidationItemQueue.TryPeek([MaybeNullWhen(false)] out IValidationItem result)
	{
		int num = _size - 1;
		IValidationItem[] array = this._array;
		if ((uint)num >= (uint)array.Length)
		{
			result = default(IValidationItem);
			return false;
		}
		result = array[num];
		return true;
	}


	IValidationItem IValidationItemQueue.Pop()
	{
		int num = _size - 1;
		IValidationItem[] array = this._array;
		if ((uint)num >= (uint)array.Length)
		{
			ThrowForEmptyStack();
		}
		_version++;
		_size = num;
		IValidationItem result = array[num];
		if (RuntimeHelpers.IsReferenceOrContainsReferences<IValidationItem>())
		{
			array[num] = default;
		}
		return result;
	}

	bool IValidationItemQueue.TryPop([MaybeNullWhen(false)] out IValidationItem result)
	{
		int num = _size - 1;
		IValidationItem[] array = this._array;
		if ((uint)num >= (uint)array.Length)
		{
			result = default;
			return false;
		}
		_version++;
		_size = num;
		result = array[num];
		if (RuntimeHelpers.IsReferenceOrContainsReferences<IValidationItem>())
		{
			array[num] = default;
		}
		return true;
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

	public IValidationItem[] ToArray()
	{
		if (_size == 0)
		{
			return Array.Empty<IValidationItem>();
		}
		IValidationItem[] array = new IValidationItem[_size];
		for (int i = 0; i < _size; i++)
		{
			array[i] = this._array[_size - i - 1];
		}
		return array;
	}

	private void ThrowForEmptyStack()
	{
		throw new InvalidOperationException();// (System.SR.InvalidOperation_EmptyStack);
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
		private readonly ValidationItemQueue _stack;

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

		internal Enumerator(ValidationItemQueue stack)
		{
			this._stack = stack;
			this._version = stack._version;
			this._index = -2;
			this._current = default;
		}

		public void Dispose()
		{
			this._index = -1;
		}

		public bool MoveNext()
		{
			if (this._version != _stack._version)
			{
				throw new InvalidOperationException("");// System.SR.InvalidOperation_EnumFailedVersion);
			}
			bool flag;
			if (this._index == -2)
			{
				this._index = _stack._size - 1;
				flag = _index >= 0;
				if (flag)
				{
					this._current = _stack._array[_index];
				}
				return flag;
			}
			if (_index == -1)
			{
				return false;
			}
			flag = --_index >= 0;
			if (flag)
			{
				this._current = _stack._array[_index];
			}
			else
			{
				this._current = default;
			}
			return flag;
		}

		private void ThrowEnumerationNotStartedOrEnded()
		{
			throw new InvalidOperationException("");// (_index == -2) ? System.SR.InvalidOperation_EnumNotStarted : System.SR.InvalidOperation_EnumEnded);
		}

		void IEnumerator.Reset()
		{
			if (_version != _stack._version)
			{
				throw new InvalidOperationException("");// System.SR.InvalidOperation_EnumFailedVersion);
			}
			_index = -2;
			_current = default;
		}
	}
}
