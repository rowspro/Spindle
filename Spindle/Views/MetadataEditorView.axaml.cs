using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Spindle.ViewModels;

namespace Spindle.Views;

/// <summary>The reusable metadata editor — hosted in the Metadata tab and in the Inbox detail.
/// Acts on its own DataContext (a MetadataEditorViewModel), so both hosts share one editor.</summary>
public partial class MetadataEditorView : UserControl
{
    public MetadataEditorView()
    {
        InitializeComponent();
        WireGenreMultiTag();
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    // Genre multi-tag: filter on the segment after the last ';' and, on pick, replace only that segment —
    // so "Rock; " offers the full list again and selecting "Pop" yields "Rock; Pop".
    private void WireGenreMultiTag()
    {
        if (this.FindControl<AutoCompleteBox>("GenreBox") is not { } gb) return;
        gb.TextFilter = (search, item) =>
        {
            var seg = LastGenreSegment(search);
            return seg.Length == 0 || (item?.Contains(seg, StringComparison.OrdinalIgnoreCase) ?? false);
        };
        gb.TextSelector = (search, item) =>
        {
            var s = search ?? "";
            int i = s.LastIndexOf(';');
            return i >= 0 ? s.Substring(0, i + 1).TrimEnd() + " " + item : item;
        };
    }

    private static string LastGenreSegment(string? text)
    {
        if (string.IsNullOrEmpty(text)) return "";
        int i = text.LastIndexOf(';');
        return (i >= 0 ? text.Substring(i + 1) : text).Trim();
    }

    private MetadataEditorViewModel? Vm => DataContext as MetadataEditorViewModel;

    private async void OnOpenMetaFile(object? sender, RoutedEventArgs e)
    {
        var sp = TopLevel.GetTopLevel(this)?.StorageProvider;
        if (sp == null || Vm == null) return;
        var files = await sp.OpenFilePickerAsync(new FilePickerOpenOptions { Title = "Choose an audio file", AllowMultiple = false });
        var p = files.Count > 0 ? files[0].TryGetLocalPath() : null;
        if (p != null) Vm.Open(p);
    }

    private async void OnOpenMetaFolder(object? sender, RoutedEventArgs e)
    {
        var sp = TopLevel.GetTopLevel(this)?.StorageProvider;
        if (sp == null || Vm == null) return;
        var dirs = await sp.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = "Choose a folder (album)", AllowMultiple = false });
        var p = dirs.Count > 0 ? dirs[0].TryGetLocalPath() : null;
        if (p != null) Vm.LoadFolder(p);
    }

    private async void OnLoadMetaArt(object? sender, RoutedEventArgs e)
    {
        var sp = TopLevel.GetTopLevel(this)?.StorageProvider;
        if (sp == null || Vm == null) return;
        var files = await sp.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Choose album art",
            AllowMultiple = false,
            FileTypeFilter = new[] { new FilePickerFileType("Images") { Patterns = new[] { "*.jpg", "*.jpeg", "*.png" } } }
        });
        var p = files.Count > 0 ? files[0].TryGetLocalPath() : null;
        if (p != null) Vm.SetArt(p);
    }

    private async void OnPasteMetaArt(object? sender, RoutedEventArgs e)
    {
        var data = await ReadClipboardImageAsync();
        if (data != null) Vm?.SetArtBytes(data);
        else Vm?.Notify("No image on the clipboard.");
    }

    private async Task<byte[]?> ReadClipboardImageAsync()
    {
        try
        {
            var cb = TopLevel.GetTopLevel(this)?.Clipboard;
            if (cb == null) return null;
            foreach (var f in await cb.GetFormatsAsync())
            {
                var fl = f.ToLowerInvariant();
                if (!(fl.Contains("png") || fl.Contains("jpeg") || fl.Contains("jpg") || fl.Contains("tiff") || fl.Contains("image"))) continue;
                if (await cb.GetDataAsync(f) is not byte[] b || b.Length < 16) continue;
                if (fl.Contains("tiff"))
                {
                    try
                    {
                        using var ms = new System.IO.MemoryStream(b);
                        var bmp = new Avalonia.Media.Imaging.Bitmap(ms);
                        using var outMs = new System.IO.MemoryStream();
                        bmp.Save(outMs);
                        return outMs.ToArray();
                    }
                    catch { continue; }
                }
                return b;
            }
        }
        catch { }
        return null;
    }

    // Enter = accept the first suggestion; Space = accept only when the match is unique.
    private void OnArtistKeyDown(object? sender, KeyEventArgs e)
    {
        if (sender is not AutoCompleteBox box || Vm == null) return;
        if (e.Key != Key.Enter && e.Key != Key.Space) return;
        var typed = (box.SearchText ?? box.Text ?? "").TrimEnd();
        if (typed.Length < 2) return;
        var matches = Vm.ArtistSuggestions
            .Where(a => a.StartsWith(typed, StringComparison.OrdinalIgnoreCase))
            .Take(2).ToList();
        if (matches.Count == 0 || (e.Key == Key.Space && matches.Count > 1)) return;
        var m = matches[0];
        if (string.Equals(m, box.Text, StringComparison.Ordinal)) return;
        if (e.Key == Key.Enter) e.Handled = true;
        Dispatcher.UIThread.Post(() =>
        {
            box.Text = m;
            box.IsDropDownOpen = false;
            if (box.FindDescendantOfType<TextBox>() is { } tb) tb.CaretIndex = m.Length;
        }, DispatcherPriority.Input);
    }
}
