using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Stratum.Core;
using Windows.Storage.Pickers;

namespace Stratum.Shell;

internal sealed record CreateRequest(string ArchivePath, CreateOptions Options);

internal sealed record ExtractRequest(string Destination, OverwriteMode Overwrite, bool OpenFolder);

/// <summary>The app's dialogs, built in code so each one is a single awaitable call.</summary>
internal sealed class Dialogs(FrameworkElement root, nint hwnd)
{
    private static readonly (string Label, long Bytes)[] SplitSizes =
    [
        ("Don't split", 0),
        ("10 MB", 10L << 20),
        ("100 MB", 100L << 20),
        ("700 MB (CD)", 700L << 20),
        ("1 GB", 1L << 30),
        ("4 GB (FAT32 limit)", 4095L << 20),
        ("Custom size…", -1),
    ];

    private bool _open;

    private ContentDialog NewDialog(string title, object content, string primary, string? close = "Cancel") => new()
    {
        XamlRoot = root.XamlRoot,
        RequestedTheme = root.ActualTheme,
        Title = title,
        Content = content,
        PrimaryButtonText = primary,
        CloseButtonText = close ?? "",
        DefaultButton = ContentDialogButton.Primary,
    };

    private async Task<ContentDialogResult> ShowAsync(ContentDialog dialog)
    {
        // WinUI allows only one ContentDialog at a time.
        if (_open) return ContentDialogResult.None;
        _open = true;
        try { return await dialog.ShowAsync(); }
        finally { _open = false; }
    }

    public Task MessageAsync(string title, string text) =>
        ShowAsync(NewDialog(title, new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true }, "OK", null));

    public async Task<bool> ConfirmAsync(string title, string text, string action) =>
        await ShowAsync(NewDialog(title, new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap }, action)) == ContentDialogResult.Primary;

    public async Task<string?> PasswordAsync(string archiveName, string? error)
    {
        var box = new PasswordBox { PlaceholderText = "Password", Width = 360 };
        var panel = new StackPanel { Spacing = 12 };
        panel.Children.Add(new TextBlock { Text = $"“{archiveName}” is protected. Enter its password to continue.", TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(box);
        if (error is not null)
            panel.Children.Add(new InfoBar { IsOpen = true, IsClosable = false, Severity = InfoBarSeverity.Error, Message = error });

        var dialog = NewDialog("Password required", panel, "Unlock");
        dialog.IsPrimaryButtonEnabled = false;
        box.PasswordChanged += (_, _) => dialog.IsPrimaryButtonEnabled = box.Password.Length > 0;
        dialog.Opened += (_, _) => box.Focus(FocusState.Programmatic);

        return await ShowAsync(dialog) == ContentDialogResult.Primary ? box.Password : null;
    }

    public async Task<CreateRequest?> CreateAsync(IReadOnlyList<string> sources, string? lastFolder)
    {
        var first = sources[0].TrimEnd('\\', '/');
        var parent = Path.GetDirectoryName(first) ?? lastFolder ?? Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
        var baseName = sources.Count == 1
            ? (Directory.Exists(first) ? Path.GetFileName(first) : Path.GetFileNameWithoutExtension(first))
            : Path.GetFileName(parent.TrimEnd('\\'));
        if (string.IsNullOrWhiteSpace(baseName)) baseName = "Archive";

        var name = new TextBox { Header = "Archive name", Text = baseName };
        var folder = new TextBox { Header = "Save in", Text = parent, IsReadOnly = true };
        var browse = new Button { Content = "Browse…", VerticalAlignment = VerticalAlignment.Bottom };
        var format = new ComboBox { Header = "Format", ItemsSource = new[] { "ZIP", "7Z", "TAR" }, SelectedIndex = 0, HorizontalAlignment = HorizontalAlignment.Stretch };
        var strength = new ComboBox { Header = "Compression", ItemsSource = new[] { "Store (no compression)", "Fast", "Normal", "Maximum" }, SelectedIndex = 2, HorizontalAlignment = HorizontalAlignment.Stretch };
        var password = new PasswordBox { Header = "Password (optional, AES-256)" };
        var confirm = new PasswordBox { Header = "Confirm password" };
        var hideNames = new CheckBox { Content = "Also hide file names", IsEnabled = false };
        var split = new ComboBox { Header = "Split into volumes", ItemsSource = SplitSizes.Select(s => s.Label).ToList(), SelectedIndex = 0, HorizontalAlignment = HorizontalAlignment.Stretch };
        var customMb = new NumberBox { Header = "Volume size (MB)", Minimum = 1, Maximum = 1024 * 1024, Value = 50, Visibility = Visibility.Collapsed, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact };
        var error = new InfoBar { IsOpen = false, IsClosable = false, Severity = InfoBarSeverity.Error };
        var summary = new TextBlock
        {
            Text = sources.Count == 1 ? Path.GetFileName(first) : $"{sources.Count} items",
            Opacity = 0.7,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };

        void Sync()
        {
            var is7z = format.SelectedIndex == 1;
            var isTar = format.SelectedIndex == 2;
            password.IsEnabled = confirm.IsEnabled = !isTar;
            strength.IsEnabled = !isTar;
            hideNames.IsEnabled = is7z && password.Password.Length > 0;
            if (!hideNames.IsEnabled) hideNames.IsChecked = false;
            customMb.Visibility = SplitSizes[split.SelectedIndex].Bytes < 0 ? Visibility.Visible : Visibility.Collapsed;
        }
        format.SelectionChanged += (_, _) => Sync();
        split.SelectionChanged += (_, _) => Sync();
        password.PasswordChanged += (_, _) => Sync();

        browse.Click += async (_, _) =>
        {
            if (await PickFolderAsync() is { } picked) folder.Text = picked;
        };

        var folderRow = new Grid { ColumnSpacing = 8 };
        folderRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        folderRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        folderRow.Children.Add(folder);
        Grid.SetColumn(browse, 1);
        folderRow.Children.Add(browse);

        var pair = new Grid { ColumnSpacing = 12 };
        pair.ColumnDefinitions.Add(new ColumnDefinition());
        pair.ColumnDefinitions.Add(new ColumnDefinition());
        pair.Children.Add(format);
        Grid.SetColumn(strength, 1);
        pair.Children.Add(strength);

        var panel = new StackPanel { Spacing = 12, Width = 420 };
        foreach (var child in new UIElement[] { summary, name, folderRow, pair, password, confirm, hideNames, split, customMb, error })
            panel.Children.Add(child);

        var dialog = NewDialog("New archive", new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto }, "Create");
        CreateRequest? result = null;

        dialog.PrimaryButtonClick += (_, args) =>
        {
            string? problem = null;
            var fileName = name.Text.Trim();
            if (fileName.Length == 0 || fileName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) problem = "Enter a valid archive name.";
            else if (password.Password != confirm.Password) problem = "The passwords don't match.";

            var bytes = SplitSizes[split.SelectedIndex].Bytes;
            if (bytes < 0)
            {
                if (double.IsNaN(customMb.Value) || customMb.Value < 1) problem ??= "Enter a volume size of at least 1 MB.";
                else bytes = (long)(customMb.Value * 1024 * 1024);
            }

            if (problem is not null)
            {
                error.Message = problem;
                error.IsOpen = true;
                args.Cancel = true;
                return;
            }

            var chosen = format.SelectedIndex switch { 1 => CreateFormat.SevenZip, 2 => CreateFormat.Tar, _ => CreateFormat.Zip };
            var extension = CreateOptions.Extension(chosen);
            if (!fileName.EndsWith(extension, StringComparison.OrdinalIgnoreCase)) fileName += extension;

            result = new CreateRequest(Path.Combine(folder.Text, fileName), new CreateOptions
            {
                Format = chosen,
                Strength = (CompressionStrength)strength.SelectedIndex,
                Password = chosen == CreateFormat.Tar || password.Password.Length == 0 ? null : password.Password,
                EncryptFileNames = hideNames.IsChecked == true,
                VolumeSize = Math.Max(0, bytes),
            });
        };

        return await ShowAsync(dialog) == ContentDialogResult.Primary ? result : null;
    }

    public async Task<ExtractRequest?> ExtractAsync(string title, string suggestedDestination, bool openFolderDefault)
    {
        var destination = new TextBox { Header = "Extract to", Text = suggestedDestination };
        var browse = new Button { Content = "Browse…", VerticalAlignment = VerticalAlignment.Bottom };
        var overwrite = new ComboBox
        {
            Header = "If a file already exists",
            ItemsSource = new[] { "Replace it", "Skip it", "Keep both (rename)" },
            SelectedIndex = 0,
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        var open = new CheckBox { Content = "Open the folder when done", IsChecked = openFolderDefault };

        browse.Click += async (_, _) =>
        {
            if (await PickFolderAsync() is { } picked) destination.Text = picked;
        };

        var row = new Grid { ColumnSpacing = 8 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.Children.Add(destination);
        Grid.SetColumn(browse, 1);
        row.Children.Add(browse);

        var panel = new StackPanel { Spacing = 12, Width = 420 };
        panel.Children.Add(row);
        panel.Children.Add(overwrite);
        panel.Children.Add(open);

        var dialog = NewDialog(title, panel, "Extract");
        dialog.IsPrimaryButtonEnabled = destination.Text.Trim().Length > 0;
        destination.TextChanged += (_, _) => dialog.IsPrimaryButtonEnabled = destination.Text.Trim().Length > 0;

        if (await ShowAsync(dialog) != ContentDialogResult.Primary) return null;
        return new ExtractRequest(destination.Text.Trim(), (OverwriteMode)overwrite.SelectedIndex, open.IsChecked == true);
    }

    public async Task<string?> PickFolderAsync()
    {
        var picker = new FolderPicker { SuggestedStartLocation = PickerLocationId.Desktop };
        picker.FileTypeFilter.Add("*");
        WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);
        return (await picker.PickSingleFolderAsync())?.Path;
    }

    public async Task<IReadOnlyList<string>> PickFilesAsync(bool archivesOnly)
    {
        var picker = new FileOpenPicker { SuggestedStartLocation = PickerLocationId.Downloads, ViewMode = PickerViewMode.List };
        if (archivesOnly)
        {
            foreach (var ext in new[] { ".zip", ".7z", ".rar", ".tar", ".gz", ".tgz", ".bz2", ".xz", ".001", ".cab", ".iso" })
                picker.FileTypeFilter.Add(ext);
        }
        picker.FileTypeFilter.Add("*");
        WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);

        if (archivesOnly)
            return await picker.PickSingleFileAsync() is { } one ? [one.Path] : [];
        return [.. (await picker.PickMultipleFilesAsync()).Select(f => f.Path)];
    }
}
