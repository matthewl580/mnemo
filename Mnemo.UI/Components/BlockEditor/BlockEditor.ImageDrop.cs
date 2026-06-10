using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Threading.Tasks;
using Mnemo.Core.Formatting;
using Mnemo.Core.Models;
using Mnemo.Infrastructure.Common;
using Mnemo.UI.Services;

namespace Mnemo.UI.Components.BlockEditor;

public partial class BlockEditor
{
    private void Editor_DragOver_BlockGhost(object? sender, DragEventArgs e)
    {
        bool isBlockReorderDrag =
            e.DataTransfer.Contains(BlockViewModel.BlockDragDataFormat)
            || e.DataTransfer.Contains(BlockViewModel.BlockReorderDragPayload.Format);
        if (!isBlockReorderDrag)
        {
            if (TryHandleExternalImageDragOver(e))
            {
                e.DragEffects = DragDropEffects.Copy;
                e.Handled = true;
                return;
            }

            HideExternalImageDragGhost();
            e.DragEffects = DragDropEffects.None;
            return;
        }

        HideExternalImageDragGhost();

        if (!TryResolveBlockReorderPayload(e, out var payload) || payload == null)
        {
            e.DragEffects = DragDropEffects.None;
            return;
        }

        var pos = e.GetPosition(this);
        if (_blockDragGhostBorder != null && _blockDragGhostOverlay != null)
            UpdateBlockDragGhostFromEditorPoint(pos);

        e.DragEffects = DragDropEffects.Move;
        HandleBlockDragOver(pos, payload);
    }

    private static bool TryResolveBlockReorderPayload(DragEventArgs e, out BlockViewModel.BlockReorderDragPayload? payload)
    {
        if (e.DataTransfer.TryGetValue(BlockViewModel.BlockReorderDragPayload.Format) is BlockViewModel.BlockReorderDragPayload p)
        {
            payload = p;
            return true;
        }

        if (e.DataTransfer.TryGetValue(BlockViewModel.BlockDragDataFormat) is not { } vm)
        {
            payload = null;
            return false;
        }

        payload = new BlockViewModel.BlockReorderDragPayload
        {
            Primary = vm,
            BlocksInDocumentOrder = new[] { vm }
        };
        return true;
    }

    private async void Editor_Drop(object? sender, DragEventArgs e)
    {
        if (e.Handled) return;

        if (!TryResolveBlockReorderPayload(e, out var payload) || payload == null)
        {
            if (await TryDropExternalImageAsync(e).ConfigureAwait(true))
                e.Handled = true;
            return;
        }

        var dropPosInEditor = e.GetPosition(this);
        HandleBlockDragOver(dropPosInEditor, payload);

        try
        {
            TryPerformDrop(payload);
            e.Handled = true;
        }
        catch (Exception)
        {
        }
        finally
        {
            ClearDropIndicator();
        }
    }

    private void Editor_DragLeave(object? sender, DragEventArgs e)
    {
        var pos = e.GetPosition(this);
        var bounds = new Rect(0, 0, Bounds.Width, Bounds.Height);
        if (!bounds.Contains(pos))
        {
            ClearDropIndicator();
            HideExternalImageDragGhost();
        }
    }

    private bool TryHandleExternalImageDragOver(DragEventArgs e)
    {
        if (!IsExternalImageDragData(e.DataTransfer))
            return false;

        var cursor = e.GetPosition(this);
        ShowExternalImageDragGhost(cursor);
        if (TryGetUnassignedImageBlockAtPoint(cursor) != null)
        {
            ClearDropIndicator();
            return true;
        }

        var probeImage = BlockFactory.CreateBlock(BlockType.Image, 0);
        if (TryGetColumnDropInsert(cursor, probeImage, out var tc, out var leftColumn, out var insertIndex))
        {
            if (!(ReferenceEquals(ColumnDropTarget, tc)
                && ColumnDropLeft == leftColumn
                && ColumnDropInsertIndex == insertIndex
                && this.FindControl<Border>("BlockReorderDropLineOverlay") is { IsVisible: true }))
            {
                ClearDropIndicator();
                ColumnDropTarget = tc;
                ColumnDropLeft = leftColumn;
                ColumnDropInsertIndex = insertIndex;
                DropInsertIndex = -1;
                ShowColumnDropLineInOverlay(tc, leftColumn, insertIndex);
            }

            return true;
        }

        var topInsert = GetInsertIndex(cursor.Y);
        if (topInsert < 0)
            return false;

        if (DropInsertIndex != topInsert || ColumnDropTarget != null)
        {
            ClearDropIndicator();
            DropInsertIndex = topInsert;
            ShowHorizontalReorderDropLineInOverlay(topInsert);
        }

        return true;
    }

    private async Task<bool> TryDropExternalImageAsync(DragEventArgs e)
    {
        if (!IsExternalImageDragData(e.DataTransfer))
            return false;

        if (e.DataTransfer is not IAsyncDataTransfer asyncTransfer)
            return false;

        var droppedImage = await TryCreateImageBlockFromDropDataAsync(asyncTransfer).ConfigureAwait(true);
        if (droppedImage == null)
            return false;

        var cursor = e.GetPosition(this);
        try
        {
            BeginStructuralChange();

            var unassignedTarget = TryGetUnassignedImageBlockAtPoint(cursor);
            if (unassignedTarget != null)
            {
                var importedPath = await ImportImagePathForTargetAsync(droppedImage.ImagePath, unassignedTarget.Id).ConfigureAwait(true);
                if (!string.IsNullOrWhiteSpace(importedPath))
                    unassignedTarget.ImagePath = importedPath;
                else
                    unassignedTarget.ImagePath = droppedImage.ImagePath;

                unassignedTarget.ImageWidth = 0;
                unassignedTarget.SetSpans(new List<InlineSpan> { InlineSpan.Plain(string.Empty) });
                ClearBlockSelection();
                CommitStructuralChange("Drop image");
                BlocksChanged?.Invoke();
                Dispatcher.UIThread.Post(() => unassignedTarget.IsFocused = true, DispatcherPriority.Input);
                return true;
            }

            await HydratePastedImageBlocksAsync(new BlockViewModel[] { droppedImage }).ConfigureAwait(true);

            if (ColumnDropTarget != null && ColumnDropInsertIndex >= 0)
            {
                var column = ColumnDropLeft ? ColumnDropTarget.LeftColumnBlocks : ColumnDropTarget.RightColumnBlocks;
                var insertAt = Math.Clamp(ColumnDropInsertIndex, 0, column.Count);
                BlockHierarchy.WireChildOwnership(ColumnDropTarget, droppedImage, ColumnDropLeft);
                SubscribeToBlock(droppedImage);
                column.Insert(insertAt, droppedImage);
            }
            else
            {
                var insertIndex = DropInsertIndex;
                if (insertIndex < 0 || insertIndex > Blocks.Count)
                {
                    insertIndex = GetInsertIndex(cursor.Y);
                    if (insertIndex < 0)
                        insertIndex = Blocks.Count;
                }

                insertIndex = Math.Clamp(insertIndex, 0, Blocks.Count);
                SubscribeToBlock(droppedImage);
                Blocks.Insert(insertIndex, droppedImage);
                droppedImage.Order = insertIndex;
            }

            ReorderBlocks();
            ClearBlockSelection();
            CommitStructuralChange("Drop image");
            BlocksChanged?.Invoke();
            Dispatcher.UIThread.Post(() => droppedImage.IsFocused = true, DispatcherPriority.Input);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
        finally
        {
            ClearDropIndicator();
            HideExternalImageDragGhost();
        }
    }

    private async Task<BlockViewModel?> TryCreateImageBlockFromDropDataAsync(IAsyncDataTransfer data)
    {
        try
        {
            var files = await data.TryGetFilesAsync().ConfigureAwait(true);
            if (files != null)
            {
                foreach (var f in files)
                {
                    var p = f.TryGetLocalPath();
                    if (string.IsNullOrWhiteSpace(p) || !File.Exists(p)) continue;
                    if (!ClipboardImageExtensions.Contains(Path.GetExtension(p))) continue;
                    return CreateImageBlockStubForPaste(p);
                }
            }
        }
        catch
        {
        }

        var bitmap = await data.TryGetBitmapAsync().ConfigureAwait(true);
        if (bitmap != null)
        {
            try
            {
                return await SaveClipboardBitmapToNewImageBlockAsync(bitmap).ConfigureAwait(true);
            }
            finally
            {
                bitmap.Dispose();
            }
        }

        if (data is IDataTransfer syncTransfer
            && syncTransfer.TryGetValue(DataFormat.Text) is string text)
        {
            var candidate = NormalizeSingleLineImagePathFromClipboard(text);
            if (!string.IsNullOrWhiteSpace(candidate) && File.Exists(candidate)
                && ClipboardImageExtensions.Contains(Path.GetExtension(candidate)))
            {
                return CreateImageBlockStubForPaste(candidate);
            }

            var url = NormalizeSingleLineImageUrlFromDragText(text);
            if (!string.IsNullOrWhiteSpace(url))
                return await DownloadExternalImageToNewImageBlockAsync(url).ConfigureAwait(true);
        }

        try
        {
            var asyncText = await data.TryGetTextAsync().ConfigureAwait(true);
            var candidate = NormalizeSingleLineImagePathFromClipboard(asyncText);
            if (!string.IsNullOrWhiteSpace(candidate) && File.Exists(candidate)
                && ClipboardImageExtensions.Contains(Path.GetExtension(candidate)))
            {
                return CreateImageBlockStubForPaste(candidate);
            }

            var url = NormalizeSingleLineImageUrlFromDragText(asyncText);
            if (!string.IsNullOrWhiteSpace(url))
                return await DownloadExternalImageToNewImageBlockAsync(url).ConfigureAwait(true);
        }
        catch
        {
        }

        return null;
    }

    private static bool IsExternalImageDragData(IDataTransfer data)
    {
        if (data.Contains(DataFormat.File))
            return true;

        if (data.TryGetValue(DataFormat.Text) is string text)
        {
            var trimmed = text.Trim();
            if (trimmed.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                || trimmed.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                return true;
            if (NormalizeSingleLineImagePathFromClipboard(trimmed) != null)
                return true;
        }

        return data is IAsyncDataTransfer;
    }

    private BlockViewModel? TryGetUnassignedImageBlockAtPoint(Point cursorPosInEditor)
    {
        foreach (var vm in BlockHierarchy.EnumerateInDocumentOrder(Blocks))
        {
            if (vm.Type != BlockType.Image)
                continue;
            if (!string.IsNullOrWhiteSpace(vm.ImagePath))
                continue;

            var bounds = GetEditableBlockBoundsInEditor(GetEditableBlockForViewModel(vm));
            if (bounds.Width <= 0 || bounds.Height <= 0)
                continue;
            if (bounds.Contains(cursorPosInEditor))
                return vm;
        }

        return null;
    }

    private async Task<string?> ImportImagePathForTargetAsync(string? sourcePath, string targetBlockId)
    {
        if (string.IsNullOrWhiteSpace(sourcePath) || !File.Exists(sourcePath))
            return sourcePath;

        var svc = ResolveImageAssetService();
        if (svc == null)
            return sourcePath;

        try
        {
            var imported = await svc.ImportAndCopyAsync(sourcePath, targetBlockId).ConfigureAwait(true);
            if (imported.IsSuccess && !string.IsNullOrWhiteSpace(imported.Value))
                return imported.Value;
        }
        catch
        {
        }

        return sourcePath;
    }

    private void ShowExternalImageDragGhost(Point cursorPosInEditor)
    {
        var overlay = this.FindControl<LayoutOverlayPanel>("BlockDragGhostOverlay");
        if (overlay == null)
            return;

        if (_externalImageDragGhostBorder == null)
        {
            _externalImageDragGhostBorder = new Border
            {
                Width = 132,
                Height = 34,
                CornerRadius = new CornerRadius(6),
                Background = new SolidColorBrush(Color.FromArgb(230, 33, 33, 33)),
                BorderBrush = new SolidColorBrush(Color.FromArgb(200, 255, 255, 255)),
                BorderThickness = new Thickness(1),
                IsHitTestVisible = false,
                Child = new TextBlock
                {
                    Text = "Drop image",
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                    Foreground = Brushes.White,
                    FontSize = 12
                }
            };

            overlay.Children.Add(_externalImageDragGhostBorder);
        }

        Canvas.SetLeft(_externalImageDragGhostBorder, cursorPosInEditor.X + 14);
        Canvas.SetTop(_externalImageDragGhostBorder, cursorPosInEditor.Y + 14);
        _externalImageDragGhostBorder.IsVisible = true;
        overlay.InvalidateArrange();
    }

    private void HideExternalImageDragGhost()
    {
        if (_externalImageDragGhostBorder == null)
            return;
        _externalImageDragGhostBorder.IsVisible = false;
    }

    private static string? NormalizeSingleLineImageUrlFromDragText(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;

        var candidate = text
            .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(s => s.Trim())
            .FirstOrDefault(s => !string.IsNullOrWhiteSpace(s) && !s.StartsWith("#", StringComparison.Ordinal));
        if (string.IsNullOrWhiteSpace(candidate))
            return null;

        if (!Uri.TryCreate(candidate, UriKind.Absolute, out var uri))
            return null;
        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
            return null;

        return uri.ToString();
    }

    private static async Task<BlockViewModel?> DownloadExternalImageToNewImageBlockAsync(string imageUrl)
    {
        try
        {
            using var response = await ExternalImageHttpClient.GetAsync(imageUrl).ConfigureAwait(true);
            if (!response.IsSuccessStatusCode)
                return null;
            var mediaType = response.Content.Headers.ContentType?.MediaType;
            if (!string.IsNullOrWhiteSpace(mediaType)
                && !mediaType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
                return null;

            var bytes = await response.Content.ReadAsByteArrayAsync().ConfigureAwait(true);
            if (bytes.Length == 0)
                return null;

            if (mediaType is null
                || mediaType.Equals("image/png", StringComparison.OrdinalIgnoreCase)
                || mediaType.Equals("image/jpeg", StringComparison.OrdinalIgnoreCase)
                || mediaType.Equals("image/jpg", StringComparison.OrdinalIgnoreCase)
                || mediaType.Equals("image/gif", StringComparison.OrdinalIgnoreCase)
                || mediaType.Equals("image/bmp", StringComparison.OrdinalIgnoreCase)
                || mediaType.Equals("image/tiff", StringComparison.OrdinalIgnoreCase))
            {
                await using var probe = new MemoryStream(bytes, writable: false);
                try
                {
                    using var _ = new Bitmap(probe);
                }
                catch
                {
                    return null;
                }
            }

            var vm = BlockFactory.CreateBlock(BlockType.Image, 0);
            var dir = MnemoAppPaths.GetImagesDirectory();
            Directory.CreateDirectory(dir);

            var ext = GuessImageFileExtension(response.Content.Headers.ContentType?.MediaType, imageUrl);
            var path = Path.Combine(dir, vm.Id + ext);
            await File.WriteAllBytesAsync(path, bytes).ConfigureAwait(true);

            vm.ImagePath = path;
            vm.ImageWidth = 0;
            vm.SetSpans(new List<InlineSpan> { InlineSpan.Plain(string.Empty) });
            return vm;
        }
        catch
        {
            return null;
        }
    }

    private static string GuessImageFileExtension(string? mediaType, string sourceUrl)
    {
        if (!string.IsNullOrWhiteSpace(mediaType))
        {
            if (mediaType.Contains("png", StringComparison.OrdinalIgnoreCase)) return ".png";
            if (mediaType.Contains("jpeg", StringComparison.OrdinalIgnoreCase) || mediaType.Contains("jpg", StringComparison.OrdinalIgnoreCase)) return ".jpg";
            if (mediaType.Contains("gif", StringComparison.OrdinalIgnoreCase)) return ".gif";
            if (mediaType.Contains("webp", StringComparison.OrdinalIgnoreCase)) return ".webp";
            if (mediaType.Contains("bmp", StringComparison.OrdinalIgnoreCase)) return ".bmp";
            if (mediaType.Contains("tiff", StringComparison.OrdinalIgnoreCase)) return ".tiff";
        }

        var ext = Path.GetExtension(sourceUrl);
        return ClipboardImageExtensions.Contains(ext) ? ext : ".png";
    }
}
