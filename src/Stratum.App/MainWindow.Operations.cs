using System.Diagnostics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Stratum.Core;

namespace Stratum.Shell;

public sealed partial class MainWindow
{
    // ---------- command handlers ----------

    private void Open_Click(object sender, RoutedEventArgs e) => Run(async () =>
    {
        if (Busy) return;
        var picked = await _dialogs.PickFilesAsync(archivesOnly: true);
        if (picked.Count > 0) await OpenArchiveAsync(picked[0]);
    });

    private void NewFromFiles_Click(object sender, RoutedEventArgs e) => Run(async () =>
    {
        if (Busy) return;
        var picked = await _dialogs.PickFilesAsync(archivesOnly: false);
        if (picked.Count > 0) await CreateArchiveAsync(picked);
    });

    private void NewFromFolder_Click(object sender, RoutedEventArgs e) => Run(async () =>
    {
        if (Busy) return;
        if (await _dialogs.PickFolderAsync() is { } folder) await CreateArchiveAsync([folder]);
    });

    private void Extract_Click(object sender, RoutedEventArgs e) => Run(async () =>
    {
        if (_reader is null || _tree is null || Busy) return;

        var rows = SelectedRows();
        var archive = _reader.ArchivePath;
        var suggested = Path.Combine(Path.GetDirectoryName(archive)!, ArchiveBaseName(archive));
        var title = rows.Count == 0 ? "Extract everything" : rows.Count == 1 ? $"Extract “{rows[0].Name}”" : $"Extract {rows.Count} items";

        var request = await _dialogs.ExtractAsync(title, suggested, _settings.OpenFolderAfterExtract);
        if (request is null) return;
        _settings.OpenFolderAfterExtract = request.OpenFolder;
        _settings.Save();

        var indexes = rows.Count == 0 ? null : _tree.IndexesUnder(rows.Select(r => r.Item));
        if (await ExtractAsync(request.Destination, indexes, request.Overwrite))
        {
            ShowNotice($"Extracted to {request.Destination}", InfoBarSeverity.Success);
            if (request.OpenFolder) OpenInExplorer(request.Destination);
        }
    });

    private void Add_Click(object sender, RoutedEventArgs e) => Run(async () =>
    {
        if (!CanModify) return;
        var picked = await _dialogs.PickFilesAsync(archivesOnly: false);
        if (picked.Count > 0) await AddPathsAsync(picked);
    });

    private void Test_Click(object sender, RoutedEventArgs e) => Run(async () =>
    {
        if (_reader is null || Busy) return;
        var healthy = false;
        var finished = await WithPasswordAsync(() => RunJobAsync("Testing the archive", async (progress, ct) =>
        {
            healthy = await _reader!.TestAsync(progress, ct);
        }));
        if (!finished) return;

        if (healthy) ShowNotice("No errors found. The archive is intact.", InfoBarSeverity.Success);
        else await _dialogs.MessageAsync("The archive is damaged",
            "One or more items failed the integrity check. If the archive is encrypted, the password may also be wrong.");
    });

    private void Delete_Click(object sender, RoutedEventArgs e) => Run(async () =>
    {
        if (!CanModify || _tree is null) return;
        var rows = SelectedRows();
        if (rows.Count == 0) return;

        var what = rows.Count == 1 ? $"“{rows[0].Name}”" : $"{rows.Count} items";
        if (!await _dialogs.ConfirmAsync("Delete from archive?",
                $"{what} will be removed from the archive permanently. Files on your disk are not affected.", "Delete")) return;

        var path = _reader!.ArchivePath;
        var indexes = _tree.IndexesUnder(rows.Select(r => r.Item));
        var done = await RunJobAsync("Deleting from the archive", (_, ct) => ArchiveWriter.DeleteAsync(path, indexes, _password, ct));
        await ReloadAsync();
        if (done) ShowNotice($"Deleted {what}.", InfoBarSeverity.Success);
    });

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        _job?.Cancel();
        CancelButton.IsEnabled = false;
        JobDetail.Text = "Cancelling…";
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
        var done = await RunJobAsync($"Creating {Path.GetFileName(request.ArchivePath)}", async (progress, ct) =>
        {
            written = await ArchiveWriter.CreateAsync(request.ArchivePath, sources, request.Options, progress, ct);
        });
        if (!done || written.Count == 0) return;

        _settings.LastFolder = Path.GetDirectoryName(request.ArchivePath);
        _settings.Save();

        // Reopen with the password just chosen, so the user is not asked for it straight away.
        try
        {
            _password = request.Options.Password;
            SetReader(await ArchiveReader.OpenAsync(written[0], _password), resetFolder: true);
        }
        catch (ArchiveException)
        {
            await OpenArchiveAsync(written[0]);
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
        return WithPasswordAsync(() => RunJobAsync($"Extracting {name}", (progress, ct) => indexes is null
            ? _reader!.ExtractAllAsync(destination, overwrite, progress, ct)
            : _reader!.ExtractAsync(indexes, destination, folder, overwrite, progress, ct)));
    }

    /// <summary>Double-click: folders are entered, files are extracted to a temp folder and opened.</summary>
    private async Task OpenRowAsync(ItemRow row)
    {
        if (_reader is null || _tree is null || Busy) return;
        if (row.Item.IsDirectory)
        {
            NavigateTo(row.Item.FullPath);
            return;
        }
        if (row.Item.EntryIndex is not { } index) return;

        if (Format.Executables.Contains(Path.GetExtension(row.Name)) &&
            !await _dialogs.ConfirmAsync("Run this file?",
                $"“{row.Name}” can run code on your PC. Only open it if you trust where this archive came from.", "Run anyway")) return;

        var temp = Path.Combine(_tempRoot, Guid.NewGuid().ToString("N")[..8]);
        var folder = _folder;
        var ok = await WithPasswordAsync(() => RunJobAsync($"Opening {row.Name}",
            (progress, ct) => _reader!.ExtractAsync([index], temp, folder, OverwriteMode.Overwrite, progress, ct)));
        if (!ok) return;

        var file = Path.Combine(temp, row.Name);
        if (!File.Exists(file)) file = Directory.EnumerateFiles(temp, "*", SearchOption.AllDirectories).FirstOrDefault() ?? file;
        try
        {
            Process.Start(new ProcessStartInfo(file) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or FileNotFoundException or InvalidOperationException)
        {
            ShowNotice($"Windows couldn't open {row.Name}: {ex.Message}", InfoBarSeverity.Error);
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

        var cancelling = _job;
        var progress = new Progress<ArchiveProgress>(p =>
        {
            if (cancelling.IsCancellationRequested || !ReferenceEquals(_job, cancelling)) return;
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
