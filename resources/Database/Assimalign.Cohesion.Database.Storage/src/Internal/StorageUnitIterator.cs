using System;
using System.Collections;
using System.Collections.Generic;

namespace Assimalign.Cohesion.Database.Storage;

using Assimalign.Cohesion.Database.Storage.Units;

/// <summary>
/// Iterates over all non-deleted storage units (records) across data pages
/// in a storage file.
/// </summary>
internal sealed unsafe class StorageUnitIterator : IStorageUnitIterator
{
    private readonly IStoragePageManager _pageManager;
    private readonly long _pageCount;
    private long _currentPageId;
    private int _currentSlotIndex;
    private IStorageUnit? _current;
    private IStoragePageHandle? _currentHandle;

    internal StorageUnitIterator(IStoragePageManager pageManager)
    {
        _pageManager = pageManager;
        _pageCount = pageManager.PageCount;
        _currentPageId = 1; // Start after header (page 0)
        _currentSlotIndex = -1;
    }

    /// <inheritdoc />
    public IStorageUnit Current => _current!;

    /// <inheritdoc />
    object IEnumerator.Current => _current!;

    /// <inheritdoc />
    public bool Next(out IStorageUnit? unit)
    {
        if (MoveNext())
        {
            unit = _current;
            return true;
        }

        unit = null;
        return false;
    }

    /// <inheritdoc />
    public bool MoveNext()
    {
        while (_currentPageId < _pageCount)
        {
            if (_currentHandle == null)
            {
                _currentHandle = _pageManager.GetPage((PageId)_currentPageId);
            }

            if (_currentHandle.Page.Type == PageType.Data)
            {
                var slotted = new SlottedPage(_currentHandle.Page);
                _currentSlotIndex++;

                while (_currentSlotIndex < slotted.SlotCount)
                {
                    int slotLength = slotted.GetSlotLength(_currentSlotIndex);

                    if (slotLength > 0)
                    {
                        var data = new byte[slotLength];
                        slotted.ReadSlot(_currentSlotIndex, data);
                        _current = new StorageUnit((PageId)_currentPageId, _currentSlotIndex, data);
                        return true;
                    }

                    _currentSlotIndex++;
                }
            }

            _currentHandle.Dispose();
            _currentHandle = null;
            _currentPageId++;
            _currentSlotIndex = -1;
        }

        _currentHandle?.Dispose();
        _currentHandle = null;
        _current = null;
        return false;
    }

    /// <inheritdoc />
    public void Reset()
    {
        _currentHandle?.Dispose();
        _currentHandle = null;
        _currentPageId = 1;
        _currentSlotIndex = -1;
        _current = null;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _currentHandle?.Dispose();
        _currentHandle = null;
    }
}
