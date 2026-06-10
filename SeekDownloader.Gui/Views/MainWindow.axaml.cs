using System.Collections.Generic;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;
using SeekDownloader.Gui.ViewModels;

namespace SeekDownloader.Gui.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        // Tunnel so the window sees arrow keys before buttons/tabs consume them for focus navigation.
        AddHandler(KeyDownEvent, OnWindowKeyDown, Avalonia.Interactivity.RoutingStrategies.Tunnel, handledEventsToo: true);
        // Remember tool folders (and other settings) across launches.
        Closing += (_, _) => Vm?.SaveSettings();
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
        // Cmd+Z: globale undo van bestandsoperaties (niet in tekstvelden).
        if (e.Key == Key.Z && e.KeyModifiers.HasFlag(KeyModifiers.Meta) && e.Source is not TextBox)
        {
            Vm.UndoLast();
            e.Handled = true;
            return;
        }

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

        if (Vm.SelectedTabIndex != 3) return; // Metadata tab
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

    private MainViewModel? Vm => DataContext as MainViewModel;

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

    private async void OnBrowseSearchFile(object? sender, RoutedEventArgs e)
    {
        var path = await PickFileAsync("Kies een zoekbestand");
        if (path != null && Vm != null) Vm.SearchFilePath = path;
    }

    private async void OnBrowseArchiveFile(object? sender, RoutedEventArgs e)
    {
        // Save picker so you can create a NEW archive file (the open picker only allows existing files).
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Kies of maak een archiefbestand",
            SuggestedFileName = "spindle-archief.txt",
            DefaultExtension = "txt",
            FileTypeChoices = new[] { new FilePickerFileType("Tekstbestand") { Patterns = new[] { "*.txt" } } }
        });
        var path = file?.TryGetLocalPath();
        if (path != null && Vm != null) Vm.DownloadArchiveFilePath = path;
    }

    private async System.Threading.Tasks.Task<string?> PickFolderAsync(string title)
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = title,
            AllowMultiple = false
        });
        return folders.Count > 0 ? folders[0].TryGetLocalPath() : null;
    }

    private async System.Threading.Tasks.Task<string?> PickFileAsync(string title)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = title,
            AllowMultiple = false
        });
        return files.Count > 0 ? files[0].TryGetLocalPath() : null;
    }

    // ---- ALAC converter tab ----
    private async void OnBrowseAlacOutput(object? sender, RoutedEventArgs e)
    {
        var path = await PickFolderAsync("Kies een doelmap voor de ALAC-bestanden");
        if (path != null && Vm != null) Vm.Alac.OutputFolder = path;
    }

    private async void OnBrowseAlacSource(object? sender, RoutedEventArgs e)
    {
        var path = await PickFolderAsync("Kies de bronmap (muziek)");
        if (path != null && Vm != null) Vm.Alac.SourceFolder = path;
    }

    // ---- Sort tab ----
    private async void OnBrowseSortSource(object? sender, RoutedEventArgs e)
    {
        var path = await PickFolderAsync("Kies de map om te sorteren");
        if (path != null && Vm != null) Vm.Sort.SourceFolder = path;
    }

    private async void OnBrowseSortDest(object? sender, RoutedEventArgs e)
    {
        var path = await PickFolderAsync("Kies de doelmap");
        if (path != null && Vm != null) Vm.Sort.DestFolder = path;
    }

    // ---- Organiseren tab ----
    private async void OnBrowseOrganizeSource(object? sender, RoutedEventArgs e)
    {
        var path = await PickFolderAsync("Kies de map om te organiseren");
        if (path != null && Vm != null) Vm.Organize.SourceFolder = path;
    }

    private async void OnBrowseOrganizeDest(object? sender, RoutedEventArgs e)
    {
        var path = await PickFolderAsync("Kies de bibliotheek-doelmap");
        if (path != null && Vm != null) Vm.Organize.DestFolder = path;
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

    // ---- Apple Music tab ----
    private async void OnBrowseAppleLibrary(object? sender, RoutedEventArgs e)
    {
        var path = await PickFolderAsync("Kies je gedownloade-muziek-map");
        if (path != null && Vm != null) Vm.AppleMusic.LibraryFolder = path;
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
