using System.Diagnostics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.VisualBasic.FileIO;
using Stratum.Core;

namespace Stratum.Shell;

public sealed partial class MainWindow
{
    // ---------- File menu ----------

    private void Open_Click(object sender, RoutedEventArgs e) => Run(async () =>
    {
        if (Busy) return;
        var picked = await _dialogs.PickFilesAsync(archivesOnly: true);
        if (picked.Count > 0) await OpenArchiveAsync(picked[0]);
    });

    private void ChangeFolder_Click(object sender, RoutedEventArgs e) => Run(async () =>
    {
        if (Busy) return;
        if (await _dialogs.PickFolderAsync() is { } folder) ShowDiskFolder(folder);
    });

    private void CloseArchive_Click(object sender, RoutedEventArgs e)
    {
        if (InArchive && !Busy) CloseArchive();
    }

    private void SelectAll_Click(object sender, RoutedEventArgs e)
    {
        FileList.SelectAll();
        if (FileList.Items.Count > 0 && FileList.Items[0] is ItemRow { Kind: RowKind.Parent } parent)
            FileList.SelectedItems.Remove(parent);
    }

    private void Exit_Click(object sender, RoutedEventArgs e) => Close();

    // ---------- Commands ----------

    /// <summary>Add: inside an archive it adds files to it; in a folder it packs the selection into a new archive.</summary>
    private void Add_Click(object sender, RoutedEventArgs e) => Run(async () =>
    {
        if (Busy) return;
        if (InArchive)
        {
            if (!CanModify)
            {
                ShowNotice("This archive is read-only. Only ZIP, 7Z and TAR archives can be changed.", InfoBarSeverity.Warning);
                return;
            }
            var picked = await _dialogs.PickFilesAsync(archivesOnly: false);
            if (picked.Count > 0) await AddPathsAsync(picked);
            return;
        }

        var paths = SelectedRows().Where(r => r.Kind is RowKind.File or RowKind.Folder).Select(r => r.DiskPath!).ToList();
        if (paths.Count == 0)
        {
            ShowNotice("Select the files or folders you want to add to an archive.", InfoBarSeverity.Informational);
            return;
        }
        await CreateArchiveAsync(paths);
    });

    private void Extract_Click(object sender, RoutedEventArgs e) => Run(async () =>
    {
        if (Busy) return;
        var wasInArchive = InArchive;
        if (!await EnsureArchiveAsync()) return;

        // When the archive was just opened from the folder view, the old selection is gone: extract everything.
        var rows = wasInArchive ? SelectedRows() : [];
        var archive = _reader!.ArchivePath;
        var suggested = Path.Combine(Path.GetDirectoryName(archive)!, ArchiveBaseName(archive));
        var title = rows.Count == 0 ? "Extraction path and options"
            : rows.Count == 1 ? $"Extract “{rows[0].Name}”" : $"Extract {rows.Count} items";

        var request = await _dialogs.ExtractAsync(title, suggested, _settings.OpenFolderAfterExtract);
        if (request is null) return;
        _settings.OpenFolderAfterExtract = request.OpenFolder;
        _settings.Save();

        var indexes = rows.Count == 0 ? null : _tree!.IndexesUnder(rows.Where(r => r.Item is not null).Select(r => r.Item!));
        if (await ExtractAsync(request.Destination, indexes, request.Overwrite))
        {
            ShowNotice($"Extracted to {request.Destination}", InfoBarSeverity.Success);
            if (request.OpenFolder) OpenInExplorer(request.Destination);
        }
    });

    private void Test_Click(object sender, RoutedEventArgs e) => Run(async () =>
    {
        if (Busy || !await EnsureArchiveAsync()) return;
        var healthy = false;
        var finished = await WithPasswordAsync(() => RunJobAsync("Testing the archive", async (progress, ct) =>
        {
            healthy = await _reader!.TestAsync(progress, ct);
        }));
        if (!finished) return;

        if (healthy) await _dialogs.MessageAsync("Test complete", "No errors found during the test operation.");
        else await _dialogs.MessageAsync("The archive is damaged",
            "One or more items failed the integrity check. If the archive is encrypted, the password may also be wrong.");
    });

    private void View_Click(object sender, RoutedEventArgs e)
    {
        if (!Busy && SelectedRows().FirstOrDefault() is { } row) Run(() => OpenRowAsync(row));
    }

    private void Delete_Click(object sender, RoutedEventArgs e) => Run(async () =>
    {
        if (Busy) return;
        var rows = SelectedRows();
        if (rows.Count == 0) return;
        var what = rows.Count == 1 ? $"“{rows[0].Name}”" : $"{rows.Count} items";

        if (!InArchive)
        {
            var targets = rows.Where(r => r.Kind is RowKind.File or RowKind.Folder).ToList();
            if (targets.Count == 0) return;
            if (!await _dialogs.ConfirmAsync("Move to the Recycle Bin?", $"{what} will be moved to the Recycle Bin.", "Delete")) return;
            try
            {
                foreach (var row in targets)
                {
                    if (row.Kind == RowKind.Folder)
                        FileSystem.DeleteDirectory(row.DiskPath!, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin);
                    else
                        FileSystem.DeleteFile(row.DiskPath!, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or OperationCanceledException)
            {
                ShowNotice($"Some items could not be deleted: {ex.Message}", InfoBarSeverity.Error);
            }
            Refresh();
            return;
        }

        if (!CanModify)
        {
            ShowNotice("This archive is read-only. Only ZIP, 7Z and TAR archives can be changed.", InfoBarSeverity.Warning);
            return;
        }
        if (!await _dialogs.ConfirmAsync("Delete from archive?",
                $"{what} will be removed from the archive permanently. Files on your disk are not affected.", "Delete")) return;

        var path = _reader!.ArchivePath;
        var indexes = _tree!.IndexesUnder(rows.Where(r => r.Item is not null).Select(r => r.Item!));
        var done = await RunJobAsync("Deleting from the archive", (_, ct) => ArchiveWriter.DeleteAsync(path, indexes, _password, ct));
        await ReloadAsync();
        if (done) ShowNotice($"Deleted {what}.", InfoBarSeverity.Success);
    });

    private void Find_Click(object sender, RoutedEventArgs e) => FilterBox.Focus(FocusState.Programmatic);

    private void Info_Click(object sender, RoutedEventArgs e) => Run(async () =>
    {
        if (Busy || !await EnsureArchiveAsync()) return;
        var r = _reader!;
        var files = r.Entries.Count(en => !en.IsDirectory);
        var folders = r.Entries.Count(en => en.IsDirectory);
        var ratio = r.UnpackedSize > 0 ? $"{r.PackedSize * 100.0 / r.UnpackedSize:0}%" : "-";
        var encryption = r.HasPassword && !r.HasEncryptedEntries ? "Yes, including file names"
            : r.HasEncryptedEntries ? "Yes" : "None";

        await _dialogs.MessageAsync(Path.GetFileName(r.ArchivePath), string.Join("\n",
            $"Location:\t{Path.GetDirectoryName(r.ArchivePath)}",
            $"Archive format:\t{r.FormatName.ToUpperInvariant()}",
            $"Files:\t\t{files:N0}",
            $"Folders:\t{folders:N0}",
            $"Total size:\t{r.UnpackedSize:N0} bytes",
            $"Packed size:\t{r.PackedSize:N0} bytes",
            $"Ratio:\t\t{ratio}",
            $"Volumes:\t{r.VolumeCount}",
            $"Solid archive:\t{(r.IsSolid ? "Yes" : "No")}",
            $"Encryption:\t{encryption}"));
    });

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        _job?.Cancel();
        CancelButton.IsEnabled = false;
        JobDetail.Text = "Cancelling…";
    }

    /// <summary>
    /// Archive commands work on the open archive. From a folder view they work on the selected
    /// archive file, which is opened first.
    /// </summary>
    private async Task<bool> EnsureArchiveAsync()
    {
        if (InArchive) return true;
        var rows = SelectedRows();
        if (rows.Count == 1 && rows[0].Kind == RowKind.File && Format.LooksLikeArchive(rows[0].DiskPath!))
            return await OpenArchiveAsync(rows[0].DiskPath!);

        ShowNotice("Select an archive first.", InfoBarSeverity.Informational);
        return false;
    }

    // ---------- operations ----------

    private async Task CreateArchiveAsync(IReadOnlyList<string> sources)
    {
        if (Busy) return;
        var request = await _dialogs.CreateAsync(sources, _settings.LastFolder);
        if (request is null) return;

        if (File.Exists(request.ArchivePath) &&
            !await _dialogs.ConfirmAsync("Replace the existing archive?",
                $"“{Path.GetFileName(request.ArchivePath)}” already exists in that folder.", "Replace")) return;

        IReadOnlyList<string> written = [];
        var done = await RunJobAsync($"Creating archive {Path.GetFileName(request.ArchivePath)}", async (progress, ct) =>
        {
            written = await ArchiveWriter.CreateAsync(request.ArchivePath, sources, request.Options, progress, ct);
        });
        if (!done || written.Count == 0) return;

        // Like a classic archiver: stay in the folder view and show the new archive there.
        ShowDiskFolder(Path.GetDirectoryName(written[0])!);
        var created = FileList.Items.OfType<ItemRow>().FirstOrDefault(r =>
            string.Equals(r.DiskPath, written[0], StringComparison.OrdinalIgnoreCase));
        if (created is not null)
        {
            FileList.SelectedItem = created;
            FileList.ScrollIntoView(created);
        }

        ShowNotice(written.Count > 1
            ? $"Created {written.Count} volumes, starting with {Path.GetFileName(written[0])}."
            : $"Created {Path.GetFileName(written[0])}.", InfoBarSeverity.Success);
    }

    private async Task AddPathsAsync(IReadOnlyList<string> paths)
    {
        if (!CanModify) return;
        var archive = _reader!.ArchivePath;
        var folder = _folder;
        var done = await RunJobAsync("Adding to the archive",
            (progress, ct) => ArchiveWriter.AddAsync(archive, paths, folder, _password, progress, ct));
        await ReloadAsync();
        if (done) ShowNotice(paths.Count == 1 ? $"Added {Path.GetFileName(paths[0])}." : $"Added {paths.Count} items.", InfoBarSeverity.Success);
    }

    private Task<bool> ExtractAsync(string destination, IReadOnlyList<int>? indexes, OverwriteMode overwrite)
    {
        var folder = _folder;
        var name = Path.GetFileName(_reader!.ArchivePath);
        return WithPasswordAsync(() => RunJobAsync($"Extracting from {name}", (progress, ct) => indexes is null
            ? _reader!.ExtractAllAsync(destination, overwrite, progress, ct)
            : _reader!.ExtractAsync(indexes, destination, folder, overwrite, progress, ct)));
    }

    /// <summary>Double-click or Enter on a row.</summary>
    private async Task OpenRowAsync(ItemRow row)
    {
        if (Busy) return;
        switch (row.Kind)
        {
            case RowKind.Parent:
                GoUp();
                return;
            case RowKind.Folder:
            case RowKind.Drive:
                ShowDiskFolder(row.DiskPath!);
                return;
            case RowKind.File:
                if (Format.LooksLikeArchive(row.DiskPath!)) await OpenArchiveAsync(row.DiskPath!);
                else Launch(row.DiskPath!, row.Name);
                return;
        }

        if (!InArchive || row.Item is null) return;
        if (row.Item.IsDirectory)
        {
            _folder = row.Item.FullPath;
            FilterBox.Text = "";
            Refresh();
            return;
        }
        if (row.Item.EntryIndex is not { } index) return;

        if (Format.Executables.Contains(Path.GetExtension(row.Name)) &&
            !await _dialogs.ConfirmAsync("Run this file?",
                $"“{row.Name}” can run code on your PC. Only open it if you trust where this archive came from.", "Run anyway")) return;

        // Files inside an archive are extracted to a temp folder and opened from there.
        var temp = Path.Combine(_tempRoot, Guid.NewGuid().ToString("N")[..8]);
        var folder = _folder;
        var ok = await WithPasswordAsync(() => RunJobAsync($"Opening {row.Name}",
            (progress, ct) => _reader!.ExtractAsync([index], temp, folder, OverwriteMode.Overwrite, progress, ct)));
        if (!ok) return;

        var file = Path.Combine(temp, row.Name);
        if (!File.Exists(file)) file = Directory.EnumerateFiles(temp, "*", System.IO.SearchOption.AllDirectories).FirstOrDefault() ?? file;
        Launch(file, row.Name);
    }

    private void Launch(string file, string displayName)
    {
        try
        {
            Process.Start(new ProcessStartInfo(file) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or FileNotFoundException or InvalidOperationException)
        {
            ShowNotice($"Windows couldn't open {displayName}: {ex.Message}", InfoBarSeverity.Error);
        }
    }

    // ---------- job plumbing ----------

    /// <summary>
    /// Runs one long operation with the progress panel. Returns false when it failed or was cancelled
    /// (the user has already been told). Password problems are rethrown for <see cref="WithPasswordAsync"/>.
    /// </summary>
    private async Task<bool> RunJobAsync(string title, Func<IProgress<ArchiveProgress>, CancellationToken, Task> work)
    {
        if (Busy) return false;

        _job = new CancellationTokenSource();
        JobTitle.Text = title;
        JobDetail.Text = "";
        JobProgress.Value = 0;
        JobProgress.IsIndeterminate = true;
        CancelButton.IsEnabled = true;
        JobPanel.Visibility = Visibility.Visible;
        UpdateCommands();

        var current = _job;
        var progress = new Progress<ArchiveProgress>(p =>
        {
            if (current.IsCancellationRequested || !ReferenceEquals(_job, current)) return;
            JobProgress.IsIndeterminate = false;
            JobProgress.Value = p.Percent;
            if (p.CurrentItem is not null) JobDetail.Text = p.CurrentItem;
        });

        string? failure = null;
        var completed = false;
        try
        {
            await work(progress, _job.Token);
            completed = true;
        }
        catch (OperationCanceledException)
        {
            ShowNotice("Cancelled.", InfoBarSeverity.Informational);
        }
        catch (ArchiveException ex) when (ex is not PasswordRequiredException and not WrongPasswordException)
        {
            failure = ex.Message;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            failure = ex.Message;
        }
        finally
        {
            _job.Dispose();
            _job = null;
            JobPanel.Visibility = Visibility.Collapsed;
            UpdateCommands();
            UpdateStatus();
        }

        if (failure is not null) await _dialogs.MessageAsync("The operation didn't finish", failure);
        return completed;
    }

    /// <summary>Runs an action against the open archive, asking for the password (again) whenever it is needed.</summary>
    private async Task<bool> WithPasswordAsync(Func<Task<bool>> action)
    {
        while (true)
        {
            string? error;
            try
            {
                return await action();
            }
            catch (PasswordRequiredException) { error = null; }
            catch (WrongPasswordException) { error = "That password didn't work. Try again."; }

            while (true)
            {
                if (_reader is null) return false;
                var password = await _dialogs.PasswordAsync(Path.GetFileName(_reader.ArchivePath), error);
                if (password is null) return false;
                try
                {
                    var reopened = await ArchiveReader.OpenAsync(_reader.ArchivePath, password);
                    _password = password;
                    SetReader(reopened, resetFolder: false);
                    break;
                }
                catch (WrongPasswordException) { error = "That password didn't work. Try again."; }
                catch (ArchiveException ex)
                {
                    await _dialogs.MessageAsync("Can't open this file", ex.Message);
                    return false;
                }
            }
        }
    }

    private static void OpenInExplorer(string folder)
    {
        try
        {
            if (Directory.Exists(folder)) Process.Start(new ProcessStartInfo(folder) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
        }
    }
}
