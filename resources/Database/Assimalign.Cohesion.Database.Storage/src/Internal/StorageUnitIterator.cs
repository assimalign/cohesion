using System;
using System.Collections;
using System.Collections.Generic;

namespace Assimalign.Cohesion.Database.Storage;

using Assimalign.Cohesion.Database.Storage.Units;

/// <summary>
/// Iterates over all non-deleted storage units (records) across data pages
/// in a storage file, or — when constructed with an owner's page snapshot —
/// across only that owner's record chain.
/// </summary>
internal sealed unsafe class StorageUnitIterator : IStorageUnitIterator
{
    private readonly IStoragePageManager _pageManager;
    private readonly IStorageFreeSpaceMap _freeSpaceMap;
    private readonly long _pageCount;
    private readonly long[]? _ownerPages;
    private readonly ulong _ownerId;
    private long _pagePosition;
    private int _currentSlotIndex;
    private long _pagesVisited;
    private IStorageUnit? _current;
    private IStoragePageHandle? _currentHandle;

    internal StorageUnitIterator(IStoragePageManager pageManager, IStorageFreeSpaceMap freeSpaceMap)
    {
        _pageManager = pageManager;
        _freeSpaceMap = freeSpaceMap;
        _pageCount = pageManager.PageCount;
        _pagePosition = 1; // Start after header (page 0)
        _currentSlotIndex = -1;
    }

    internal StorageUnitIterator(
        IStoragePageManager pageManager,
        IStorageFreeSpaceMap freeSpaceMap,
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
        while (_pagePosition < _pageCount)
        {
            long pageId = _ownerPages is null ? _pagePosition : _ownerPages[_pagePosition];

            if (!_freeSpaceMap.IsAllocated((PageId)pageId))
            {
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
        _current = null;
        return false;
    }

    /// <inheritdoc />
    public void Reset()
    {
        _currentHandle?.Dispose();
        _currentHandle = null;
        _pagePosition = _ownerPages is null ? 1 : 0;
        _currentSlotIndex = -1;
        _pagesVisited = 0;
        _current = null;
    }

    /// <inheritdoc />
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
