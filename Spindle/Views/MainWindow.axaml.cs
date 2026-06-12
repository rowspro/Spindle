using System.Collections.Generic;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Spindle.ViewModels;

namespace Spindle.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        // Tunnel so the window sees arrow keys before buttons/tabs consume them for focus navigation.
        AddHandler(KeyDownEvent, OnWindowKeyDown, Avalonia.Interactivity.RoutingStrategies.Tunnel, handledEventsToo: true);
        // Remember tool folders (and other settings) across launches.
        Closing += (_, _) => { Vm?.Player.Stop(); Vm?.SaveSettings(); };
        // Fase 5: pas het opgeslagen thema toe en volg de toggle.
        DataContextChanged += (_, _) => { HookTheme(); WireArtistBoxes(); WireFormatBar(); };
        HookTheme();
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    // Arrow keys drive the Metadata tab's step-through (but not while typing in a field).
    private void OnWindowKeyDown(object? sender, KeyEventArgs e)
    {
        if (Vm == null) return;

        // Cmd+F: open the command palette from anywhere.
        if (e.Key == Key.F && e.KeyModifiers.HasFlag(KeyModifiers.Meta))
        {
            Vm.OpenPalette();
            var box = this.FindControl<TextBox>("PaletteBox");
            Avalonia.Threading.Dispatcher.UIThread.Post(() => box?.Focus());
            e.Handled = true;
            return;
        }
        // Spatie: 10s audio-preview in de Bibliotheek-browser.
        if (e.Key == Key.Space && e.Source is not TextBox && !Vm.IsPaletteOpen
            && (Vm.SelectedTabIndex == 6 || Vm.Player.HasTrack))
        {
            if (Vm.SelectedTabIndex == 6) Vm.PlayBrowserSelection();
            else Vm.Player.PlayPause();
            e.Handled = true;
            return;
        }

        // Cmd+Z: globale undo van bestandsoperaties (niet in tekstvelden).
        if (e.Key == Key.Z && e.KeyModifiers.HasFlag(KeyModifiers.Meta) && e.Source is not TextBox)
        {
            Vm.UndoLast();
            e.Handled = true;
            return;
        }

        if (Vm.IsHistoryOpen && e.Key == Key.Escape) { Vm.CloseHistory(); e.Handled = true; return; }

        if (Vm.IsPaletteOpen)
        {
            switch (e.Key)
            {
                case Key.Escape: Vm.ClosePalette(); e.Handled = true; return;
                case Key.Enter: Vm.RunSelectedPalette(); e.Handled = true; return;
                case Key.Down: Vm.MovePaletteSelection(1); e.Handled = true; return;
                case Key.Up: Vm.MovePaletteSelection(-1); e.Handled = true; return;
            }
        }

        // Inbox: J/K = cursor, Enter = inspecteur, A = vinkje, X = verwijderen, Esc = terug.
        if (Vm.SelectedTabIndex == 5 && e.Source is not TextBox && !Vm.IsPaletteOpen)
        {
            var st = Vm.Staging;
            if (st.ShowDetail)
            {
                if (e.Key == Key.Escape) { st.BackCommand.Execute(null); e.Handled = true; return; }
            }
            else
            {
                switch (e.Key)
                {
                    case Key.J: st.CursorMove(1); ScrollInboxCursor(); e.Handled = true; return;
                    case Key.K: st.CursorMove(-1); ScrollInboxCursor(); e.Handled = true; return;
                    case Key.Enter: st.CursorFix(); e.Handled = true; return;
                    case Key.A: st.CursorToggle(); e.Handled = true; return;
                    case Key.X: st.CursorDelete(); e.Handled = true; return;
                }
            }
        }

        if (Vm.SelectedTabIndex != 0) return; // Metadata tab
        if (e.Source is TextBox) return;
        if (e.Key == Key.Left)
        {
            if (Vm.Meta.BackCommand.CanExecute(null)) Vm.Meta.BackCommand.Execute(null);
            e.Handled = true;
        }
        else if (e.Key == Key.Right)
        {
            if (Vm.Meta.ApproveNextCommand.CanExecute(null)) Vm.Meta.ApproveNextCommand.Execute(null);
            e.Handled = true;
        }
    }

    // Double-click a Library Doctor finding to open its album in the Metadata editor for inspection.
    private void OnDoctorFindingDoubleTapped(object? sender, TappedEventArgs e)
    {
        if ((sender as Control)?.DataContext is DoctorFinding f)
            f.EditCommand?.Execute(null);
    }

    private MainViewModel? Vm => DataContext as MainViewModel;

    private bool _themeHooked;

    private void HookTheme()
    {
        if (_themeHooked || Vm == null) return;
        _themeHooked = true;
        ApplyTheme();
        Vm.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(MainViewModel.DarkMode)) ApplyTheme(); };
    }

    private void ApplyTheme()
    {
        if (Vm != null) RequestedThemeVariant = Vm.DarkMode ? ThemeVariant.Dark : ThemeVariant.Light;
    }

    private void OnToggleTheme(object? sender, RoutedEventArgs e)
    {
        if (Vm != null) Vm.DarkMode = !Vm.DarkMode;
    }

    private void OnPaletteBackdrop(object? sender, Avalonia.Input.PointerPressedEventArgs e) => Vm?.ClosePalette();

    // Sidebar navigation: each nav button carries its tab index in Tag.
    private void OnNav(object? sender, RoutedEventArgs e)
    {
        if (Vm != null && sender is Control c && c.Tag is string s && int.TryParse(s, out var i))
            Vm.SelectedTabIndex = i;
    }

    private async void OnBrowseDownloadFolder(object? sender, RoutedEventArgs e)
    {
        var path = await PickFolderAsync("Kies een downloadmap");
        if (path != null && Vm != null) Vm.DownloadFilePath = path;
    }

    private async void OnBrowseMusicLibrary(object? sender, RoutedEventArgs e)
    {
        var path = await PickFolderAsync("Kies je muziekbibliotheek");
        if (path != null && Vm != null) Vm.MusicLibrary = path;
    }

    private async void OnBrowseAlacMirror(object? sender, RoutedEventArgs e)
    {
        var path = await PickFolderAsync("Choose the ALAC mirror folder (outside your library)");
        if (path != null && Vm != null) Vm.AlacMirrorFolder = path;
    }

    private async Task<string?> PickFolderAsync(string title)
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = title,
            AllowMultiple = false
        });
        return folders.Count > 0 ? folders[0].TryGetLocalPath() : null;
    }

    private async Task<string?> PickFileAsync(string title)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = title,
            AllowMultiple = false
        });
        return files.Count > 0 ? files[0].TryGetLocalPath() : null;
    }

    // ---- Metadata editor tab ----
    private async void OnOpenMetaFile(object? sender, RoutedEventArgs e)
    {
        var path = await PickFileAsync("Kies een audiobestand");
        if (path != null && Vm != null) Vm.Meta.Open(path);
    }

    private async void OnOpenMetaFolder(object? sender, RoutedEventArgs e)
    {
        var path = await PickFolderAsync("Kies een map (album) om door te lopen");
        if (path != null && Vm != null) Vm.Meta.LoadFolder(path);
    }

    // ---- Duplicates tab ----
    private async void OnBrowseDupFolder(object? sender, RoutedEventArgs e)
    {
        var path = await PickFolderAsync("Kies een map om op dubbele te doorzoeken");
        if (path != null && Vm != null) Vm.Duplicates.Folder = path;
    }

    // ---- Transfer (iPod) tab ----
    private async void OnBrowseSyncLibrary(object? sender, RoutedEventArgs e)
    {
        var path = await PickFolderAsync("Kies je muziekbibliotheek");
        if (path != null && Vm != null) Vm.Sync.LibraryFolder = path;
    }

    private async void OnBrowseSyncIpod(object? sender, RoutedEventArgs e)
    {
        var path = await PickFolderAsync("Kies de iPod-map");
        if (path != null && Vm != null) Vm.Sync.IpodFolder = path;
    }

    // ---- Health: formaat-mix-balk ----
    private bool _formatBarWired;
    private void WireFormatBar()
    {
        if (Vm == null || _formatBarWired) return;
        _formatBarWired = true;
        Vm.Library.Formats.CollectionChanged += (_, _) => RebuildFormatBar();
        RebuildFormatBar();
    }

    private void RebuildFormatBar()
    {
        if (this.FindControl<Grid>("FormatBar") is not { } grid || Vm == null) return;
        grid.ColumnDefinitions.Clear();
        grid.Children.Clear();
        var segs = Vm.Library.Formats;
        for (int i = 0; i < segs.Count; i++)
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition(segs[i].Count, GridUnitType.Star));
            var b = new Border { Background = segs[i].Brush };
            ToolTip.SetTip(b, segs[i].Label);
            Grid.SetColumn(b, i);
            grid.Children.Add(b);
        }
    }

    // ---- Activity register ----
    private void OnOpenHistory(object? sender, RoutedEventArgs e) => Vm?.OpenHistory();
    private void OnCloseHistory(object? sender, RoutedEventArgs e) => Vm?.CloseHistory();
    private void OnHistoryBackdrop(object? sender, PointerPressedEventArgs e) => Vm?.CloseHistory();
    private void OnUndoTop(object? sender, RoutedEventArgs e) => Vm?.UndoTop();

    // ---- Wantlist ----
    private void OnFollowKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && Vm != null) { Vm.Wantlist.FollowCommand.Execute(null); e.Handled = true; }
    }

    private async void OnWantCopy(object? sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.DataContext is WantAlbumViewModel w && Clipboard != null)
        {
            await Clipboard.SetTextAsync(w.SearchTerm);
            Vm?.Wantlist.Notify($"Copied \"{w.SearchTerm}\" — paste it into Nicotine+.");
        }
    }

    private void ScrollInboxCursor()
    {
        if (this.FindControl<ListBox>("InboxList") is { } lb && lb.SelectedItem != null)
            lb.ScrollIntoView(lb.SelectedItem);
    }

    // ---- Album-artist autocomplete (consistent capitalization) ----
    private void WireArtistBoxes()
    {
        if (Vm == null) return;
        foreach (var name in new[] { "MetaAlbumArtistBox", "LibArtistBox" })
            if (this.FindControl<AutoCompleteBox>(name) is { } box)
                box.ItemsSource = Vm.ArtistSuggestions;
    }

    /// <summary>Enter = accept the first suggestion; Space = accept only when the match is unique
    /// (so typing a brand-new multi-word artist still works).</summary>
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
        // Na de input-pipeline zetten, zodat een eventueel ingevoegde spatie wordt overschreven.
        Dispatcher.UIThread.Post(() =>
        {
            box.Text = m;
            box.IsDropDownOpen = false;
            if (box.FindDescendantOfType<TextBox>() is { } tb) tb.CaretIndex = m.Length;
        }, DispatcherPriority.Input);
    }

    // ---- Artwork: klembord (Mp3tag-stijl) ----
    private async System.Threading.Tasks.Task<byte[]?> ReadClipboardImageAsync()
    {
        try
        {
            var cb = Clipboard;
            if (cb == null) return null;
            var formats = await cb.GetFormatsAsync();
            foreach (var f in formats)
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

    private async void OnPasteMetaArt(object? sender, RoutedEventArgs e)
    {
        var data = await ReadClipboardImageAsync();
        if (data != null) Vm?.Meta.SetArtBytes(data);
        else Vm?.Meta.Notify("No image on the clipboard.");
    }

    private async void OnInboxCoverChoose(object? sender, RoutedEventArgs e)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Choose album art",
            AllowMultiple = false,
            FileTypeFilter = new[] { new FilePickerFileType("Images") { Patterns = new[] { "*.jpg", "*.jpeg", "*.png" } } }
        });
        var p = files.Count > 0 ? files[0].TryGetLocalPath() : null;
        if (p != null && Vm != null)
        {
            try { Vm.Staging.ApplyCoverToDetailAlbum(System.IO.File.ReadAllBytes(p)); }
            catch { Vm.Staging.Notify("Couldn't read that image."); }
        }
    }

    private async void OnInboxCoverPaste(object? sender, RoutedEventArgs e)
    {
        var data = await ReadClipboardImageAsync();
        if (data != null) Vm?.Staging.ApplyCoverToDetailAlbum(data);
        else Vm?.Staging.Notify("No image on the clipboard.");
    }

    private async void OnLoadMetaArt(object? sender, RoutedEventArgs e)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Kies een albumhoes",
            AllowMultiple = false,
            FileTypeFilter = new[]
            {
                new FilePickerFileType("Afbeeldingen") { Patterns = new[] { "*.jpg", "*.jpeg", "*.png" } }
            }
        });
        var p = files.Count > 0 ? files[0].TryGetLocalPath() : null;
        if (p != null && Vm != null) Vm.Meta.SetArt(p);
    }

    private async System.Threading.Tasks.Task<List<string>> PickFilesAsync(string title)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = title,
            AllowMultiple = true
        });
        return files.Select(f => f.TryGetLocalPath()).Where(p => p != null).Select(p => p!).ToList();
    }
}
