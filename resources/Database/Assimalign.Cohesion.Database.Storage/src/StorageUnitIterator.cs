using System;
using System.Collections;
using System.Collections.Generic;

namespace Assimalign.Cohesion.Database.Storage;

using Assimalign.Cohesion.Database.Storage.Units;

/// <summary>
/// A depth-first iterator that enumerates the live <see cref="StorageUnit"/> records across
/// the data pages of a storage file, or — when obtained for an owner
/// (<see cref="Storage.GetUnitIterator(ulong)"/>) — across only that owner's record chain.
/// </summary>
/// <remarks>
/// The iterator pins at most one page at a time; disposing it releases that pin.
/// </remarks>
public sealed unsafe class StorageUnitIterator : IEnumerator<StorageUnit>
{
    private readonly StoragePageManager _pageManager;
    private readonly StorageFreeSpaceMap _freeSpaceMap;
    private readonly long _pageCount;
    private readonly long[]? _ownerPages;
    private readonly ulong _ownerId;
    private long _pagePosition;
    private int _currentSlotIndex;
    private long _pagesVisited;
    private StorageUnit _current;
    private StoragePageHandle? _currentHandle;

    internal StorageUnitIterator(StoragePageManager pageManager, StorageFreeSpaceMap freeSpaceMap)
    {
        _pageManager = pageManager;
        _freeSpaceMap = freeSpaceMap;
        _pageCount = pageManager.PageCount;
        _pagePosition = 1; // Start after header (page 0)
        _currentSlotIndex = -1;
    }

    internal StorageUnitIterator(
        StoragePageManager pageManager,
        StorageFreeSpaceMap freeSpaceMap,
        long[] ownerPages,
        ulong ownerId)
    {
        _pageManager = pageManager;
        _freeSpaceMap = freeSpaceMap;
        _ownerPages = ownerPages;
        _ownerId = ownerId;
        _pageCount = ownerPages.Length;
        _pagePosition = 0;
        _currentSlotIndex = -1;
    }

    /// <summary>
    /// Gets the number of pages this iterator has pinned so far — the scan-cost
    /// observable per-owner iteration exists to shrink (test observability).
    /// </summary>
    internal long PagesVisited => _pagesVisited;

    /// <summary>
    /// Gets the unit at the current position.
    /// </summary>
    /// <remarks>
    /// The default unit before the first <see cref="MoveNext"/> and after the scan is exhausted.
    /// </remarks>
    public StorageUnit Current => _current;

    /// <inheritdoc />
    object IEnumerator.Current => _current;

    /// <summary>
    /// Attempts to advance to the next storage unit.
    /// </summary>
    /// <param name="unit">When this method returns <c>true</c>, contains the next unit; otherwise, the default unit.</param>
    /// <returns><c>true</c> if the iterator advanced to a valid unit; otherwise, <c>false</c>.</returns>
    public bool Next(out StorageUnit unit)
    {
        if (MoveNext())
        {
            unit = _current;
            return true;
        }

        unit = default;
        return false;
    }

    /// <summary>
    /// Advances to the next live unit, pinning the page that holds it.
    /// </summary>
    /// <returns><c>true</c> if the iterator advanced to a valid unit; otherwise, <c>false</c>.</returns>
    public bool MoveNext()
    {
        while (_pagePosition < _pageCount)
        {
            long pageId = _ownerPages is null ? _pagePosition : _ownerPages[_pagePosition];

            if (!_freeSpaceMap.IsAllocated((PageId)pageId))
            {
                _currentHandle?.Dispose();
                _currentHandle = null;
                AdvancePage();
                continue;
            }

            if (_currentHandle == null)
            {
                _currentHandle = _pageManager.GetPage((PageId)pageId);
                _pagesVisited++;
            }

            // An owner-scoped scan verifies membership under the pin: a page freed
            // and reallocated after the snapshot was taken no longer matches and is
            // skipped rather than misread.
            bool eligible = _currentHandle.Page.Type == PageType.Data
                && (_ownerPages is null || _currentHandle.Page.OwnerId == _ownerId);

            if (eligible)
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
                        _current = new StorageUnit((PageId)pageId, _currentSlotIndex, data);
                        return true;
                    }

                    _currentSlotIndex++;
                }
            }

            _currentHandle.Dispose();
            _currentHandle = null;
            AdvancePage();
        }

        _currentHandle?.Dispose();
        _currentHandle = null;
        _current = default;
        return false;
    }

    /// <summary>
    /// Returns the iterator to its start, releasing the page it pins.
    /// </summary>
    public void Reset()
    {
        _currentHandle?.Dispose();
        _currentHandle = null;
        _pagePosition = _ownerPages is null ? 1 : 0;
        _currentSlotIndex = -1;
        _pagesVisited = 0;
        _current = default;
    }

    /// <summary>
    /// Releases the page the iterator pins.
    /// </summary>
    public void Dispose()
    {
        _currentHandle?.Dispose();
        _currentHandle = null;
    }

    private void AdvancePage()
    {
        _pagePosition++;
        _currentSlotIndex = -1;
    }
}
