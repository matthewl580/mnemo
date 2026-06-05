using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using System.Text.Json;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Microsoft.Extensions.DependencyInjection;
using Mnemo.Core.Models;
using Mnemo.Core.Services;
using Mnemo.UI.Components.Overlays;
using Mnemo.UI.Modules.Notes.ViewModels;

namespace Mnemo.UI.Modules.Notes.Views;

public partial class NotesView
{
    private async void OnExportPdfClick(object? sender, RoutedEventArgs e)
    {
        var app = Application.Current as App;
        var services = app?.Services;
        if (services == null || DataContext is not NotesViewModel vm || vm.SelectedNote == null)
            return;
        var overlayService = services.GetService<IOverlayService>();
        if (overlayService == null)
            return;
        await FlushEditorToSelectedNoteAsync().ConfigureAwait(true);
        var note = vm.SelectedNote;
        var json = JsonSerializer.Serialize(note);
        var clone = JsonSerializer.Deserialize<Note>(json);
        if (clone == null)
            return;
        var overlay = new NotePdfExportOverlay();
        overlay.InitializeForNote(clone);
        var overlayId = overlayService.CreateOverlay(overlay, new OverlayOptions
        {
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
            ShowBackdrop = true,
            CloseOnOutsideClick = true
        }, "NotePdfExport");
        overlay.CloseRequested = () => overlayService.CloseOverlay(overlayId);
    }

    private async void OnTransferClick(object? sender, RoutedEventArgs e)
    {
        var app = Application.Current as App;
        var services = app?.Services;
        var vm = DataContext as NotesViewModel;
        if (services == null || vm == null)
            return;

        var coordinator = services.GetService<IImportExportCoordinator>();
        var overlayService = services.GetService<IOverlayService>();
        if (coordinator == null || overlayService == null)
            return;

        var capabilities = coordinator.GetCapabilities("notes");
        var overlay = new TransferOverlay();
        var canExportSelectedNote = vm.SelectedNote != null;
        overlay.IsExportAvailable = canExportSelectedNote;
        overlay.SetLocalizedChrome(
            "TransferOverlayTitle", "Notes",
            "TransferOverlayDescription", "Notes",
            "Continue", "Common",
            "Cancel", "Common");
        overlay.Initialize(capabilities, defaultImport: !canExportSelectedNote);

        var overlayId = overlayService.CreateOverlay(overlay, new OverlayOptions
        {
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
            ShowBackdrop = true,
            CloseOnOutsideClick = true
        }, "TransferOverlay");

        var tcs = new TaskCompletionSource<TransferOverlayResult?>();
        overlay.OnResult = result =>
        {
            overlayService.CloseOverlay(overlayId);
            tcs.TrySetResult(result);
        };

        var selected = await tcs.Task.ConfigureAwait(true);
        if (selected == null)
            return;

        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel?.StorageProvider == null)
            return;

        if (selected.IsImport)
        {
            var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                AllowMultiple = false,
                Title = "Import notes",
                FileTypeFilter = [new FilePickerFileType(selected.Format.DisplayName) { Patterns = selected.Format.Extensions.Select(ext => $"*{ext}").ToArray() }]
            });
            var file = files.FirstOrDefault();
            if (file == null)
                return;

            var preview = await coordinator.PreviewImportAsync(new ImportExportRequest
            {
                ContentType = "notes",
                FormatId = selected.Format.FormatId,
                FilePath = file.Path.LocalPath
            }).ConfigureAwait(true);
            if (!preview.IsSuccess || preview.Value == null)
            {
                await overlayService.CreateDialogAsync("Import failed", preview.ErrorMessage ?? "Could not preview file.").ConfigureAwait(true);
                return;
            }

            var summary = string.Join(", ", preview.Value.DiscoveredCounts.Select(p => $"{p.Value} {p.Key}"));
            var confirm = await overlayService.CreateDialogAsync("Confirm Import", $"This file contains: {summary}", "Import", "Cancel").ConfigureAwait(true);
            if (!string.Equals(confirm, "Import", StringComparison.Ordinal))
                return;

            var result = await coordinator.ImportAsync(new ImportExportRequest
            {
                ContentType = "notes",
                FormatId = selected.Format.FormatId,
                FilePath = file.Path.LocalPath,
                Options = new Dictionary<string, object?>
                {
                    ["DuplicateOnConflict"] = selected.DuplicateOnConflict,
                    ["StrictUnknownPayloads"] = selected.StrictUnknownPayloads
                }
            }).ConfigureAwait(true);

            await overlayService.CreateDialogAsync(result.IsSuccess ? "Import complete" : "Import failed",
                result.IsSuccess ? "Notes import finished." : result.ErrorMessage ?? "Import failed.").ConfigureAwait(true);
            if (result.IsSuccess)
                await vm.LoadNotesCommand.ExecuteAsync(null);
            return;
        }

        if (vm.SelectedNote == null)
            return;

        await FlushEditorToSelectedNoteAsync().ConfigureAwait(true);

        var saveFile = await topLevel.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Export notes",
            SuggestedFileName = $"notes{selected.Format.Extensions.FirstOrDefault() ?? ".mnemo"}",
            DefaultExtension = selected.Format.Extensions.FirstOrDefault()?.TrimStart('.'),
            FileTypeChoices = [new FilePickerFileType(selected.Format.DisplayName) { Patterns = selected.Format.Extensions.Select(ext => $"*{ext}").ToArray() }]
        });
        if (saveFile == null)
            return;

        var exportResult = await coordinator.ExportAsync(new ImportExportRequest
        {
            ContentType = "notes",
            FormatId = selected.Format.FormatId,
            FilePath = saveFile.Path.LocalPath,
            Payload = vm.SelectedNote
        }).ConfigureAwait(true);

        await overlayService.CreateDialogAsync(exportResult.IsSuccess ? "Export complete" : "Export failed",
            exportResult.IsSuccess ? "Notes export finished." : exportResult.ErrorMessage ?? "Export failed.").ConfigureAwait(true);
    }
}
