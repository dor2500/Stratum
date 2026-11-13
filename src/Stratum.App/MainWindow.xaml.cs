using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Stratum.Core;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using VirtualKey = Windows.System.VirtualKey;

namespace Stratum.Shell;

/// <summary>
/// The main window. Like a classic archiver it is always showing a file list: either a folder
/// on disk, or the inside of an archive. ".." walks up, and out of an archive into its folder.
/// </summary>
public sealed partial class MainWindow : Window
{
    private readonly StartupRequest _startup;
    private readonly AppSettings _settings = AppSettings.Load();
    private readonly Dialogs _dialogs;
    private readonly DispatcherQueueTimer _noticeTimer;
    private readonly string _tempRoot = Path.Combine(Path.GetTempPath(), "Stratum", Guid.NewGuid().ToString("N")[..8]);

    private ArchiveReader? _reader;
    private ArchiveTree? _tree;
    private string? _password;
    private string _folder = "";       // folder inside the open archive
    private string _diskFolder = "";   // folder on disk; "" lists the drives
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
        AppWindow.Resize(new Windows.Graphics.SizeInt32((int)(1040 * scale), (int)(700 * scale)));
        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.PreferredMinimumWidth = (int)(820 * scale);
            presenter.PreferredMinimumHeight = (int)(460 * scale);
        }

        _dialogs = new Dialogs(Root, hwnd);
        _noticeTimer = DispatcherQueue.CreateTimer();
        _noticeTimer.Interval = TimeSpan.FromSeconds(5);
        _noticeTimer.IsRepeating = false;
        _noticeTimer.Tick += (_, _) => Notice.IsOpen = false;

        var start = _settings.LastFolder;
        _diskFolder = start is not null && Directory.Exists(start)
            ? start
            : Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);

        ApplyTheme(_settings.Theme);
        Closed += (_, _) => CleanTemp();
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(nint hwnd);

    private bool Busy => _job is not null;
    private bool InArchive => _reader is not null && _tree is not null;

    /// <summary>ZIP, 7Z and TAR can be changed in place; RAR, split and other formats are read-only.</summary>
    private bool CanModify => _reader is not null && !Busy &&
        !SplitVolumeStream.IsFirstVolume(_reader.ArchivePath) &&
        Path.GetExtension(_reader.ArchivePath).ToLowerInvariant() is ".zip" or ".7z" or ".tar";

    // ---------- startup ----------

    private void Root_Loaded(object sender, RoutedEventArgs e)
    {
        ExplorerToggle.IsChecked = ShellIntegration.IsRegistered;
        Refresh();
        Run(async () =>
        {
            switch (_startup.Mode)
            {
                case StartupMode.Open:
                    if (Directory.Exists(_startup.Paths[0])) ShowDiskFolder(_startup.Paths[0]);
                    else await OpenArchiveAsync(_startup.Paths[0]);
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
        _diskFolder = Path.GetDirectoryName(reader.ArchivePath) ?? "";
        _settings.LastFolder = _diskFolder;
        _settings.Save();
        SetReader(reader, resetFolder: true);
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

    private void CloseArchive()
    {
        _reader = null;
        _tree = null;
        _password = null;
        _folder = "";
        FilterBox.Text = "";
        Refresh();
    }

    private void ShowDiskFolder(string folder)
    {
        _reader = null;
        _tree = null;
        _password = null;
        _folder = "";
        _diskFolder = folder;
        FilterBox.Text = "";
        if (folder.Length > 0)
        {
            _settings.LastFolder = folder;
            _settings.Save();
        }
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

    private void Refresh()
    {
        var rows = new List<ItemRow>();
        string? problem = null;

        if (InArchive)
        {
            rows.AddRange(_tree!.List(_folder).Select(ItemRow.FromArchive));
        }
        else if (_diskFolder.Length == 0)
        {
            rows.AddRange(DriveInfo.GetDrives().Select(ItemRow.FromDrive));
        }
        else
        {
            try
            {
                var dir = new DirectoryInfo(_diskFolder);
                var options = new EnumerationOptions { IgnoreInaccessible = true, AttributesToSkip = System.IO.FileAttributes.Hidden | System.IO.FileAttributes.System };
                rows.AddRange(dir.EnumerateDirectories("*", options).Select(ItemRow.FromFolder));
                rows.AddRange(dir.EnumerateFiles("*", options).Select(ItemRow.FromFile));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                problem = ex.Message;
            }
        }

        var filter = FilterBox.Text.Trim();
        if (filter.Length > 0) rows = [.. rows.Where(r => r.Name.Contains(filter, StringComparison.OrdinalIgnoreCase))];

        Func<ItemRow, IComparable?> key = _sortKey switch
        {
            "size" => r => r.Size,
            "modified" => r => r.Modified ?? DateTime.MinValue,
            "type" => r => r.TypeText,
            "crc" => r => r.Crc,
            _ => r => r.Name.ToLowerInvariant(),
        };
        var folders = rows.Where(r => r.IsDirectory);
        var files = rows.Where(r => !r.IsDirectory);
        var sorted = (_sortDescending
            ? folders.OrderByDescending(key).Concat(files.OrderByDescending(key))
            : folders.OrderBy(key).Concat(files.OrderBy(key))).ToList();

        var canGoUp = InArchive || _diskFolder.Length > 0;
        if (canGoUp) sorted.Insert(0, ItemRow.Parent());

        FileList.ItemsSource = sorted;
        var empty = sorted.Count == (canGoUp ? 1 : 0);
        EmptyFolderText.Visibility = empty || problem is not null ? Visibility.Visible : Visibility.Collapsed;
        EmptyFolderText.Text = problem ?? (filter.Length > 0 ? "No matches" : "This folder is empty");

        if (InArchive)
        {
            var archive = _reader!.ArchivePath;
            TitleText.Text = $"{Path.GetFileName(archive)} - Stratum";
            PathBox.Text = _folder.Length == 0 ? archive : archive + "\\" + _folder.Replace('/', '\\');
        }
        else
        {
            TitleText.Text = "Stratum";
            PathBox.Text = _diskFolder.Length == 0 ? "This PC" : _diskFolder;
        }
        UpButton.IsEnabled = canGoUp;

        var folderCount = sorted.Count(r => r.IsDirectory && r.Kind != RowKind.Parent);
        var fileRows = sorted.Where(r => !r.IsDirectory).ToList();
        TotalText.Text = $"Total {Plural(folderCount, "folder")} and {fileRows.Sum(r => r.Size):N0} bytes in {Plural(fileRows.Count, "file")}";

        UpdateCommands();
        UpdateStatus();
    }

    private static string Plural(int count, string noun) => $"{count:N0} {noun}{(count == 1 ? "" : "s")}";

    private void UpdateStatus()
    {
        var selected = SelectedRows();
        if (selected.Count > 0)
        {
            var bytes = selected.Where(r => !r.IsDirectory).Sum(r => r.Size);
            StatusText.Text = $"Selected {Plural(selected.Count, "item")}, {bytes:N0} bytes";
            return;
        }

        if (_reader is null)
        {
            StatusText.Text = "";
            return;
        }

        var parts = new List<string> { $"{_reader.FormatName.ToUpperInvariant()} archive", $"unpacked size {_reader.UnpackedSize:N0} bytes" };
        if (_reader.UnpackedSize > 0) parts.Add($"ratio {_reader.PackedSize * 100.0 / _reader.UnpackedSize:0}%");
        if (_reader.VolumeCount > 1) parts.Add($"{_reader.VolumeCount} volumes");
        if (_reader.IsSolid) parts.Add("solid");
        if (_reader.HasEncryptedEntries || _reader.HasPassword) parts.Add("encrypted");
        if (!CanModify && !Busy) parts.Add("read-only");
        StatusText.Text = string.Join(", ", parts);
    }

    private void UpdateCommands()
    {
        var hasSelection = FileList.SelectedItems.OfType<ItemRow>().Any(r => r.Kind != RowKind.Parent);
        SetEnabled(AddButton, !Busy);
        SetEnabled(ExtractButton, !Busy);
        SetEnabled(TestButton, !Busy);
        SetEnabled(ViewButton, !Busy && hasSelection);
        SetEnabled(DeleteButton, !Busy && hasSelection && (!InArchive || CanModify));
        SetEnabled(FindButton, true);
        SetEnabled(InfoButton, !Busy);
        CloseArchiveItem.IsEnabled = InArchive && !Busy;
    }

    /// <summary>The toolbar icons carry their own colours, so a disabled button is dimmed as a whole.</summary>
    private static void SetEnabled(Button button, bool enabled)
    {
        button.IsEnabled = enabled;
        button.Opacity = enabled ? 1 : 0.4;
    }

    /// <summary>The selected rows, never including "..".</summary>
    private List<ItemRow> SelectedRows() => [.. FileList.SelectedItems.OfType<ItemRow>().Where(r => r.Kind != RowKind.Parent)];

    private void GoUp()
    {
        if (InArchive)
        {
            if (_folder.Length == 0)
            {
                CloseArchive();
                return;
            }
            var slash = _folder.LastIndexOf('/');
            _folder = slash < 0 ? "" : _folder[..slash];
            FilterBox.Text = "";
            Refresh();
            return;
        }

        if (_diskFolder.Length == 0) return;
        ShowDiskFolder(Directory.GetParent(_diskFolder.TrimEnd('\\'))?.FullName ?? "");
    }

    private void Up_Click(object sender, RoutedEventArgs e)
    {
        if (!Busy) GoUp();
    }

    private void PathBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Enter || Busy) return;
        e.Handled = true;
        var text = PathBox.Text.Trim().Trim('"');
        if (text.Length == 0 || text.Equals("This PC", StringComparison.OrdinalIgnoreCase)) ShowDiskFolder("");
        else if (Directory.Exists(text)) ShowDiskFolder(Path.GetFullPath(text));
        else if (File.Exists(text)) Run(() => OpenArchiveAsync(text));
        else
        {
            ShowNotice("That folder or archive doesn't exist.", InfoBarSeverity.Warning);
            Refresh();
        }
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

    private void FileList_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        switch (e.Key)
        {
            case VirtualKey.Enter when FileList.SelectedItem is ItemRow row:
                e.Handled = true;
                Run(() => OpenRowAsync(row));
                break;
            case VirtualKey.Back:
                e.Handled = true;
                Up_Click(sender, e);
                break;
            case VirtualKey.Delete when DeleteButton.IsEnabled:
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
        DropText.Text = CanModify ? "Drop to add to this archive" : "Drop to open or compress";
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

    /// <summary>
    /// Dragging rows out to Explorer. Files on disk are handed over directly; items inside an
    /// archive are extracted to a temp folder only if the drop actually happens.
    /// </summary>
    private void FileList_DragItemsStarting(object sender, DragItemsStartingEventArgs e)
    {
        var rows = e.Items.OfType<ItemRow>().Where(r => r.Kind is RowKind.ArchiveItem or RowKind.File or RowKind.Folder).ToList();
        if (Busy || rows.Count == 0)
        {
            e.Cancel = true;
            return;
        }

        var reader = _reader;
        var folder = _folder;
        IReadOnlyList<int> indexes = [];
        if (InArchive)
        {
            indexes = _tree!.IndexesUnder(rows.Where(r => r.Item is not null).Select(r => r.Item!));
            if (!reader!.HasPassword && reader.Entries.Any(en => en.Encrypted && indexes.Contains(en.Index)))
            {
                e.Cancel = true;
                ShowNotice("These items are encrypted. Use Extract To to enter the password.", InfoBarSeverity.Warning);
                return;
            }
        }

        _internalDrag = true;
        e.Data.RequestedOperation = DataPackageOperation.Copy;
        e.Data.SetDataProvider(StandardDataFormats.StorageItems, async request =>
        {
            var deferral = request.GetDeferral();
            try
            {
                var paths = new List<string>();
                if (reader is not null && indexes.Count > 0)
                {
                    var temp = Path.Combine(_tempRoot, Guid.NewGuid().ToString("N")[..8]);
                    await reader.ExtractAsync(indexes, temp, folder);
                    paths.AddRange(rows.Select(r => Path.Combine(temp, r.Name)));
                }
                else
                {
                    paths.AddRange(rows.Where(r => r.DiskPath is not null).Select(r => r.DiskPath!));
                }

                var items = new List<IStorageItem>();
                foreach (var path in paths)
                {
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

    // ---------- options ----------

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
        "with the unRAR restriction. Stratum is not affiliated with 7-Zip, RARLAB or WinRAR."));

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
