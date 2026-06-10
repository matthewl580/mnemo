using System;
using System.Collections.Generic;
using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.VisualTree;
using Mnemo.Core.Models;

namespace Mnemo.UI.Components.BlockEditor;

public partial class BlockEditor
{
    private const double BlockItemContentChromeInset = 76;

    private readonly Dictionary<int, double> _rowMeasuredHeights = new();
    private bool _blockRowVirtualizationWired;

    /// <summary>
    /// Stable minimum height for the block list, derived from per-row hints rather than
    /// ItemsRepeater's average-height estimate (which breaks for tall image rows).
    /// </summary>
    public double GetEstimatedBlockListHeight()
    {
        if (BlockRows.Count == 0)
            return BlockRowLayoutHeights.BelowBlocksArea;

        var total = BlockRowLayoutHeights.BelowBlocksArea;
        for (var i = 0; i < BlockRows.Count; i++)
        {
            total += BlockRowLayoutHeights.ResolveRowHeight(BlockRows[i], _rowMeasuredHeights, i);
            total += BlockRowLayoutHeights.RowSpacing;
        }

        if (BlockRows.Count > 0)
            total -= BlockRowLayoutHeights.RowSpacing;

        return total;
    }

    internal void ApplyZoomVirtualizationPolicy(double zoom)
    {
        if (BlocksItemsControl == null)
            return;

        // At high zoom the effective realization window shrinks; keep a larger buffer so tall
        // rows above the viewport stay measured and scroll extent does not oscillate.
        BlocksItemsControl.VerticalCacheLength = zoom switch
        {
            >= 6 => 24,
            >= 3 => 12,
            >= 1.5 => 6,
            _ => 2
        };
    }

    private void SetupBlockRowHeightVirtualization()
    {
        if (_blockRowVirtualizationWired || BlocksItemsControl == null)
            return;

        BlocksItemsControl.ElementPrepared += OnBlockRowElementPrepared;
        BlocksItemsControl.ElementClearing += OnBlockRowElementClearing;
        SizeChanged += OnBlockEditorSizeChangedForRowHeights;
        _blockRowVirtualizationWired = true;
    }

    private void TeardownBlockRowHeightVirtualization()
    {
        if (!_blockRowVirtualizationWired || BlocksItemsControl == null)
            return;

        BlocksItemsControl.ElementPrepared -= OnBlockRowElementPrepared;
        BlocksItemsControl.ElementClearing -= OnBlockRowElementClearing;
        SizeChanged -= OnBlockEditorSizeChangedForRowHeights;
        _blockRowVirtualizationWired = false;
    }

    internal void ClearBlockRowHeightCache()
    {
        _rowMeasuredHeights.Clear();
    }

    internal void RefreshAllRowLayoutHeightHints()
    {
        var colW = GetBlockContentColumnWidth();
        foreach (var row in BlockRows)
            BlockRowLayoutHeights.Refresh(row, colW);
    }

    private double GetBlockContentColumnWidth()
    {
        var w = Bounds.Width > 1 ? Bounds.Width : double.NaN;
        if (double.IsNaN(w) || w <= 1)
            w = 800;
        return Math.Max(200, w - EditorContentPaddingX * 2 - BlockItemContentChromeInset);
    }

    private void OnBlockEditorSizeChangedForRowHeights(object? sender, SizeChangedEventArgs e)
    {
        if (Math.Abs(e.NewSize.Width - e.PreviousSize.Width) <= 0.5)
            return;

        RefreshAllRowLayoutHeightHints();
        InvalidateBlockListMeasure();
    }

    private void OnBlockRowElementPrepared(object? sender, ItemsRepeaterElementPreparedEventArgs e)
    {
        if (e.Element is not Control container)
            return;

        ApplyRowHeightHintToContainer(container, e.Index);
        container.LayoutUpdated -= OnBlockRowContainerLayoutUpdated;
        container.LayoutUpdated += OnBlockRowContainerLayoutUpdated;
    }

    private void OnBlockRowElementClearing(object? sender, ItemsRepeaterElementClearingEventArgs e)
    {
        if (e.Element is Control container)
            container.LayoutUpdated -= OnBlockRowContainerLayoutUpdated;
    }

    private void OnBlockRowContainerLayoutUpdated(object? sender, EventArgs e)
    {
        if (sender is not Control container || BlocksItemsControl == null)
            return;

        var index = BlocksItemsControl.GetElementIndex(container);
        if (index < 0)
            return;

        var h = container.Bounds.Height;
        if (h <= 1)
            return;

        if (_rowMeasuredHeights.TryGetValue(index, out var prev) && Math.Abs(prev - h) <= 0.5)
            return;

        _rowMeasuredHeights[index] = h;
        if (index < BlockRows.Count)
            BlockRows[index].SetLayoutHeightHint(Math.Max(BlockRows[index].LayoutHeightHint, h));

        InvalidateBlockListMeasure();
    }

    private void ApplyRowHeightHintToContainer(Control container, int index)
    {
        var hint = index >= 0 && index < BlockRows.Count
            ? BlockRowLayoutHeights.ResolveRowHeight(BlockRows[index], _rowMeasuredHeights, index)
            : 0;

        if (hint > 1)
            container.MinHeight = hint;
        else
            container.ClearValue(Control.MinHeightProperty);

        // ContentControl recycling can skip re-measure; force a fresh pass (ItemsRepeater #4569).
        container.InvalidateMeasure();
        foreach (var child in container.GetVisualChildren())
        {
            if (child is Layoutable layoutable)
                layoutable.InvalidateMeasure();
        }
    }

    private void InvalidateRowLayoutHeightForBlock(BlockViewModel? block)
    {
        if (block == null)
            return;

        for (var i = 0; i < BlockRows.Count; i++)
        {
            var row = BlockRows[i];
            if (row is not SingleBlockRowViewModel single || !ReferenceEquals(single.Block, block))
                continue;

            _rowMeasuredHeights.Remove(i);
            BlockRowLayoutHeights.Refresh(row, GetBlockContentColumnWidth());
            if (BlocksItemsControl?.TryGetElement(i) is Control container)
                ApplyRowHeightHintToContainer(container, i);
            InvalidateBlockListMeasure();
            return;
        }
    }

    internal void InvalidateBlockListMeasure()
    {
        BlocksItemsControl?.InvalidateMeasure();
        InvalidateMeasure();
    }

    internal void OnBlockLayoutAffectingPropertyChanged(BlockViewModel block, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is not (nameof(BlockViewModel.ImageWidth)
            or nameof(BlockViewModel.ImagePath)
            or nameof(BlockViewModel.ImageAlign)
            or nameof(BlockViewModel.SketchWidth)
            or nameof(BlockViewModel.SketchAlign)
            or nameof(BlockViewModel.Content)
            or nameof(BlockViewModel.Type)))
            return;

        InvalidateRowLayoutHeightForBlock(block);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var size = base.MeasureOverride(availableSize);
        var floor = GetEstimatedBlockListHeight();
        if (floor > size.Height + 0.5)
            return new Size(size.Width, floor);
        return size;
    }
}
