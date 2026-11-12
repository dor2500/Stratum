using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Stratum.Core;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;

namespace Stratum.Shell;

public sealed partial class MainWindow : Window
{
    private readonly StartupRequest _startup;
    private readonly AppSettings _settings = AppSettings.Load();
    private readonly Dialogs _dialogs;
    private readonly DispatcherQueueTimer _noticeTimer;
    private readonly string _tempRoot = Path.Combine(Path.GetTempPath(), "Stratum", Guid.NewGuid().ToString("N")[..8]);

    private ArchiveReader? _reader;
    private ArchiveTree? _tree;
    private string _folder = "";
    private string _sortKey = "name";
    private bool _sortDescending;
    private bool _internalDrag;
    private CancellationTokenSource? _job;

    public MainWindow(StartupRequest startup)
    {
        _startup = startup;
        InitializeComponent();

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        var icon = Path.Combine(AppContext.BaseDirectory, "Assets", "Stratum.ico");
        if (File.Exists(icon)) AppWindow.SetIcon(icon);
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        var scale = GetDpiForWindow(hwnd) / 96.0;
        AppWindow.Resize(new Windows.Graphics.SizeInt32((int)(1100 * scale), (int)(720 * scale)));
        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.PreferredMinimumWidth = (int)(760 * scale);
            presenter.PreferredMinimumHeight = (int)(480 * scale);
        }

        _dialogs = new Dialogs(Root, hwnd);
        _noticeTimer = DispatcherQueue.CreateTimer();
        _noticeTimer.Interval = TimeSpan.FromSeconds(5);
        _noticeTimer.IsRepeating = false;
        _noticeTimer.Tick += (_, _) => Notice.IsOpen = false;

        ApplyTheme(_settings.Theme);
        Closed += (_, _) => CleanTemp();
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(nint hwnd);

    private bool Busy => _job is not null;

    /// <summary>ZIP, 7Z and TAR can be changed in place; RAR, split and other formats are read-only.</summary>
    private bool CanModify => _reader is not null && !Busy &&
        !SplitVolumeStream.IsFirstVolume(_reader.ArchivePath) &&
        Path.GetExtension(_reader.ArchivePath).ToLowerInvariant() is ".zip" or ".7z" or ".tar";

    // ---------- startup ----------

    private void Root_Loaded(object sender, RoutedEventArgs e)
    {
        ExplorerToggle.IsChecked = ShellIntegration.IsRegistered;
        Run(async () =>
        {
            switch (_startup.Mode)
            {
                case StartupMode.Open:
                    await OpenArchiveAsync(_startup.Paths[0]);
                    break;
                case StartupMode.Add:
                    await CreateArchiveAsync(_startup.Paths);
                    break;
                case StartupMode.ExtractHere:
                case StartupMode.ExtractToFolder:
                    var allOk = true;
                    foreach (var archive in _startup.Paths)
                    {
                        if (!await OpenArchiveAsync(archive)) { allOk = false; continue; }
                        var parent = Path.GetDirectoryName(archive)!;
                        var destination = _startup.Mode == StartupMode.ExtractHere ? parent : Path.Combine(parent, ArchiveBaseName(archive));
                        allOk &= await ExtractAsync(destination, null, OverwriteMode.Rename);
                    }
                    if (allOk) Close();
                    break;
            }
        });
    }

    // ---------- opening and browsing ----------

    private async Task<bool> OpenArchiveAsync(string path)
    {
        if (Busy) return false;
        string? password = null;
        string? error = null;
        ArchiveReader reader;
        while (true)
        {
            try
            {
                reader = await ArchiveReader.OpenAsync(path, password);
                break;
            }
            catch (PasswordRequiredException) { error = null; }
            catch (WrongPasswordException) { error = "That password didn't work. Try again."; }
            catch (ArchiveException ex)
            {
                await _dialogs.MessageAsync("Can't open this file", ex.Message);
                return false;
            }

            password = await _dialogs.PasswordAsync(Path.GetFileName(path), error);
            if (password is null) return false;
        }

        _password = password;
        SetReader(reader, resetFolder: true);
        _settings.LastFolder = Path.GetDirectoryName(reader.ArchivePath);
        _settings.Save();
        return true;
    }

    private void SetReader(ArchiveReader reader, bool resetFolder)
    {
        _reader = reader;
        _tree = new ArchiveTree(reader.Entries);
        if (resetFolder || !_tree.FolderExists(_folder)) _folder = "";
        FilterBox.Text = "";
        Refresh();
    }

    private async Task ReloadAsync()
    {
        if (_reader is null) return;
        try
        {
            SetReader(await ArchiveReader.OpenAsync(_reader.ArchivePath, _password), resetFolder: false);
        }
        catch (ArchiveException ex)
        {
            await _dialogs.MessageAsync("Can't reload the archive", ex.Message);
        }
    }

    private string? _password;

    private void Refresh()
    {
        var open = _reader is not null && _tree is not null;
        EmptyState.Visibility = open ? Visibility.Collapsed : Visibility.Visible;
        Browser.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
        NavBar.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
        UpdateCommands();

        if (!open)
        {
            TitleText.Text = "Stratum";
            StatusText.Text = "";
            return;
        }

        var name = Path.GetFileName(_reader!.ArchivePath);
        TitleText.Text = $"{name} – Stratum";

        IEnumerable<TreeItem> items = _tree!.List(_folder);
        var filter = FilterBox.Text.Trim();
        if (filter.Length > 0) items = items.Where(i => i.Name.Contains(filter, StringComparison.OrdinalIgnoreCase));

        Func<TreeItem, IComparable?> key = _sortKey switch
        {
            "size" => i => i.Size,
            "modified" => i => i.Modified ?? DateTime.MinValue,
            _ => i => i.Name.ToLowerInvariant(),
        };
        var folders = items.Where(i => i.IsDirectory);
        var files = items.Where(i => !i.IsDirectory);
        var sorted = _sortDescending
            ? folders.OrderByDescending(key).Concat(files.OrderByDescending(key))
            : folders.OrderBy(key).Concat(files.OrderBy(key));

        var rows = sorted.Select(i => new ItemRow(i)).ToList();
        FileList.ItemsSource = rows;
        EmptyFolderText.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        EmptyFolderText.Text = filter.Length > 0 ? "No matches" : "This folder is empty";

        var crumbs = new List<string> { name };
        if (_folder.Length > 0) crumbs.AddRange(_folder.Split('/'));
        Crumbs.ItemsSource = crumbs;
        UpButton.IsEnabled = _folder.Length > 0;

        UpdateStatus();
    }

    private void UpdateStatus()
    {
        if (_reader is null) return;
        var files = _reader.Entries.Count(e => !e.IsDirectory);
        var ratio = _reader.UnpackedSize > 0 ? $" ({_reader.PackedSize * 100.0 / _reader.UnpackedSize:0}%)" : "";
        var parts = new List<string>
        {
            $"{files:N0} {(files == 1 ? "file" : "files")}",
            $"{Format.Size(_reader.UnpackedSize)} → {Format.Size(_reader.PackedSize)}{ratio}",
            _reader.FormatName.ToUpperInvariant(),
        };
        if (_reader.VolumeCount > 1) parts.Add($"{_reader.VolumeCount} volumes");
        if (_reader.HasEncryptedEntries || _reader.HasPassword) parts.Add("Encrypted");
        if (!CanModify && !Busy) parts.Add("Read-only");

        var selected = FileList.SelectedItems.Count;
        if (selected > 0) parts.Insert(0, $"{selected} selected");
        StatusText.Text = string.Join("  ·  ", parts);
    }

    private void UpdateCommands()
    {
        var open = _reader is not null;
        OpenButton.IsEnabled = !Busy;
        NewButton.IsEnabled = !Busy;
        ExtractButton.IsEnabled = open && !Busy;
        TestButton.IsEnabled = open && !Busy;
        AddButton.IsEnabled = CanModify;
        DeleteButton.IsEnabled = CanModify && FileList.SelectedItems.Count > 0;
        ExtractButton.Label = open && FileList.SelectedItems.Count > 0 ? "Extract selected" : "Extract";
    }

    private List<ItemRow> SelectedRows() => [.. FileList.SelectedItems.OfType<ItemRow>()];

    private void NavigateTo(string folder)
    {
        _folder = folder;
        FilterBox.Text = "";
        Refresh();
    }

    private void Up_Click(object sender, RoutedEventArgs e)
    {
        if (_folder.Length == 0) return;
        var slash = _folder.LastIndexOf('/');
        NavigateTo(slash < 0 ? "" : _folder[..slash]);
    }

    private void Crumbs_ItemClicked(BreadcrumbBar sender, BreadcrumbBarItemClickedEventArgs args)
    {
        var parts = _folder.Length == 0 ? [] : _folder.Split('/');
        NavigateTo(string.Join('/', parts.Take(args.Index)));
    }

    private void FilterBox_TextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        if (args.Reason == AutoSuggestionBoxTextChangeReason.UserInput) Refresh();
    }

    private void Sort_Click(object sender, RoutedEventArgs e)
    {
        var key = (string)((FrameworkElement)sender).Tag;
        _sortDescending = _sortKey == key && !_sortDescending;
        _sortKey = key;
        Refresh();
    }

    private void FileList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        UpdateCommands();
        UpdateStatus();
    }

    private void FileList_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if ((e.OriginalSource as FrameworkElement)?.DataContext is ItemRow row) Run(() => OpenRowAsync(row));
    }

    private void ContextOpen_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedRows().FirstOrDefault() is { } row) Run(() => OpenRowAsync(row));
    }

    private void FileList_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        switch (e.Key)
        {
            case Windows.System.VirtualKey.Enter when SelectedRows().FirstOrDefault() is { } row:
                e.Handled = true;
                Run(() => OpenRowAsync(row));
                break;
            case Windows.System.VirtualKey.Back:
                e.Handled = true;
                Up_Click(sender, e);
                break;
            case Windows.System.VirtualKey.Delete when DeleteButton.IsEnabled:
                e.Handled = true;
                Delete_Click(sender, e);
                break;
        }
    }

    // ---------- drag and drop ----------

    private void Root_DragOver(object sender, DragEventArgs e)
    {
        if (_internalDrag || Busy || !e.DataView.Contains(StandardDataFormats.StorageItems))
        {
            e.AcceptedOperation = DataPackageOperation.None;
            return;
        }
        e.AcceptedOperation = DataPackageOperation.Copy;
        DropText.Text = _reader is null ? "Drop to open or compress"
            : CanModify ? "Drop to add to this archive" : "Drop to open or compress";
        DropOverlay.Opacity = 1;
    }

    private void Root_DragLeave(object sender, DragEventArgs e) => DropOverlay.Opacity = 0;

    private void Root_Drop(object sender, DragEventArgs e)
    {
        DropOverlay.Opacity = 0;
        if (_internalDrag || Busy || !e.DataView.Contains(StandardDataFormats.StorageItems)) return;

        var deferral = e.GetDeferral();
        Run(async () =>
        {
            List<string> paths;
            try
            {
                var items = await e.DataView.GetStorageItemsAsync();
                paths = [.. items.Select(i => i.Path).Where(p => !string.IsNullOrEmpty(p))];
            }
            finally
            {
                deferral.Complete();
            }

            if (paths.Count == 0) return;
            if (paths.Count == 1 && Format.LooksLikeArchive(paths[0])) await OpenArchiveAsync(paths[0]);
            else if (CanModify) await AddPathsAsync(paths);
            else await CreateArchiveAsync(paths);
        });
    }

    /// <summary>Dragging rows out to Explorer: the files are extracted only if the drop actually happens.</summary>
    private void FileList_DragItemsStarting(object sender, DragItemsStartingEventArgs e)
    {
        var rows = e.Items.OfType<ItemRow>().ToList();
        if (_reader is null || _tree is null || Busy || rows.Count == 0)
        {
            e.Cancel = true;
            return;
        }

        var reader = _reader;
        var folder = _folder;
        var indexes = _tree.IndexesUnder(rows.Select(r => r.Item));
        if (!reader.HasPassword && reader.Entries.Any(en => en.Encrypted && indexes.Contains(en.Index)))
        {
            e.Cancel = true;
            ShowNotice("These items are encrypted. Use Extract to enter the password.", InfoBarSeverity.Warning);
            return;
        }

        _internalDrag = true;
        e.Data.RequestedOperation = DataPackageOperation.Copy;
        e.Data.SetDataProvider(StandardDataFormats.StorageItems, async request =>
        {
            var deferral = request.GetDeferral();
            try
            {
                var temp = Path.Combine(_tempRoot, Guid.NewGuid().ToString("N")[..8]);
                await reader.ExtractAsync(indexes, temp, folder);

                var items = new List<IStorageItem>();
                foreach (var row in rows)
                {
                    var path = Path.Combine(temp, row.Name);
                    if (Directory.Exists(path)) items.Add(await StorageFolder.GetFolderFromPathAsync(path));
                    else if (File.Exists(path)) items.Add(await StorageFile.GetFileFromPathAsync(path));
                }
                request.SetData(items);
            }
            catch (Exception)
            {
                // The drop target simply receives nothing.
            }
            finally
            {
                deferral.Complete();
            }
        });
    }

    private void FileList_DragItemsCompleted(ListViewBase sender, DragItemsCompletedEventArgs args) => _internalDrag = false;

    // ---------- settings ----------

    private void Theme_Click(object sender, RoutedEventArgs e)
    {
        _settings.Theme = (string)((FrameworkElement)sender).Tag;
        _settings.Save();
        ApplyTheme(_settings.Theme);
    }

    private void ApplyTheme(string theme)
    {
        Root.RequestedTheme = theme switch
        {
            "Light" => ElementTheme.Light,
            "Dark" => ElementTheme.Dark,
            _ => ElementTheme.Default,
        };
        AppWindow.TitleBar.PreferredTheme = theme switch
        {
            "Light" => TitleBarTheme.Light,
            "Dark" => TitleBarTheme.Dark,
            _ => TitleBarTheme.UseDefaultAppMode,
        };
        ThemeSystem.IsChecked = theme is not ("Light" or "Dark");
        ThemeLight.IsChecked = theme == "Light";
        ThemeDark.IsChecked = theme == "Dark";
    }

    private void ExplorerToggle_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (ExplorerToggle.IsChecked) ShellIntegration.Register();
            else ShellIntegration.Unregister();
            ShowNotice(ExplorerToggle.IsChecked
                ? "Added to the Explorer right-click menu (under “Show more options” on Windows 11)."
                : "Removed from the Explorer right-click menu.", InfoBarSeverity.Success);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
            ExplorerToggle.IsChecked = ShellIntegration.IsRegistered;
            ShowNotice($"Explorer integration could not be changed: {ex.Message}", InfoBarSeverity.Error);
        }
    }

    private void About_Click(object sender, RoutedEventArgs e) => Run(() => _dialogs.MessageAsync("About Stratum",
        $"Stratum {typeof(MainWindow).Assembly.GetName().Version?.ToString(3)}\n" +
        "A modern archive manager for Windows.\n\n" +
        "Creates ZIP, 7Z and TAR. Opens those plus RAR and many other formats.\n\n" +
        "Compression is provided by the 7-Zip engine (7z.dll), © Igor Pavlov, licensed under the GNU LGPL " +
        "with the unRAR restriction. Stratum is not affiliated with 7-Zip or with RARLAB."));

    // ---------- helpers ----------

    /// <summary>Runs a UI task from an event handler, turning any stray exception into an error dialog.</summary>
    private async void Run(Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (Exception ex)
        {
            try { await _dialogs.MessageAsync("Something went wrong", ex.Message); }
            catch (Exception) { }
        }
    }

    private void ShowNotice(string message, InfoBarSeverity severity)
    {
        Notice.Message = message;
        Notice.Severity = severity;
        Notice.IsOpen = true;
        _noticeTimer.Stop();
        _noticeTimer.Start();
    }

    private static string ArchiveBaseName(string path)
    {
        var name = Path.GetFileName(path);
        if (name.EndsWith(".001", StringComparison.OrdinalIgnoreCase)) name = name[..^4];
        var stem = Path.GetFileNameWithoutExtension(name);
        if (stem.EndsWith(".tar", StringComparison.OrdinalIgnoreCase)) stem = stem[..^4];
        return stem.Length > 0 ? stem : "Extracted";
    }

    private void CleanTemp()
    {
        try { if (Directory.Exists(_tempRoot)) Directory.Delete(_tempRoot, true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
