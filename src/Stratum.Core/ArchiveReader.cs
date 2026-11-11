using SevenZip;

namespace Stratum.Core;

/// <summary>
/// Read-side access to an archive: listing, extraction and integrity testing.
/// Every operation opens its own engine handle, so an instance can be used from any thread.
/// </summary>
public sealed class ArchiveReader
{
    private readonly string? _password;

    private ArchiveReader(string path, string? password)
    {
        ArchivePath = path;
        _password = string.IsNullOrEmpty(password) ? null : password;
    }

    public string ArchivePath { get; }
    public string FormatName { get; private set; } = "";
    public IReadOnlyList<ArchiveEntry> Entries { get; private set; } = [];
    public bool IsSolid { get; private set; }
    public int VolumeCount { get; private set; } = 1;
    public long UnpackedSize { get; private set; }
    public long PackedSize { get; private set; }
    public bool HasPassword => _password is not null;
    public bool HasEncryptedEntries => Entries.Any(e => e.Encrypted);

    public static Task<ArchiveReader> OpenAsync(string path, string? password = null, CancellationToken ct = default) =>
        Task.Run(() => Open(path, password), ct);

    public static ArchiveReader Open(string path, string? password = null)
    {
        path = Path.GetFullPath(path);
        if (!File.Exists(path)) throw new ArchiveException($"File not found: {path}");
        if (SplitVolumeStream.IsLaterVolume(path, out var first)) path = first;

        var reader = new ArchiveReader(path, password);
        try
        {
            reader.Load();
        }
        catch (Exception ex) when (ex is not ArchiveException)
        {
            throw reader.TranslateOpenFailure(ex);
        }
        return reader;
    }

    private void Load()
    {
        using var handle = CreateExtractor();
        var extractor = handle.Extractor;

        var entries = new List<ArchiveEntry>(extractor.ArchiveFileData.Count);
        long unpacked = 0;
        foreach (var f in extractor.ArchiveFileData)
        {
            if (string.IsNullOrEmpty(f.FileName)) continue;
            DateTime? modified = f.LastWriteTime.Year > 1601 ? f.LastWriteTime : null;
            entries.Add(new ArchiveEntry(f.Index, f.FileName, f.IsDirectory, (long)f.Size, modified, f.Encrypted, f.Crc, f.Method));
            if (!f.IsDirectory) unpacked += (long)f.Size;
        }

        Entries = entries;
        UnpackedSize = unpacked;
        FormatName = extractor.Format.ToString();
        IsSolid = SafeGet(() => extractor.IsSolid);

        if (handle.Volumes is { } volumes)
        {
            VolumeCount = volumes.VolumeCount;
            PackedSize = volumes.Length;
        }
        else
        {
            var names = SafeGet(() => extractor.VolumeFileNames?.Where(File.Exists).ToList()) ?? [];
            if (names.Count == 0) names.Add(ArchivePath);
            VolumeCount = names.Count;
            PackedSize = names.Sum(n => new FileInfo(n).Length);
        }
    }

    /// <summary>Extracts everything into <paramref name="destination"/>.</summary>
    public Task ExtractAllAsync(string destination, OverwriteMode overwrite = OverwriteMode.Overwrite,
        IProgress<ArchiveProgress>? progress = null, CancellationToken ct = default) =>
        Task.Run(() => ExtractCore(null, destination, "", overwrite, progress, ct), ct);

    /// <summary>
    /// Extracts the given entries. <paramref name="stripPrefix"/> is the folder inside the archive
    /// that the selection is relative to, so "docs/a/b.txt" with prefix "docs" lands at "a/b.txt".
    /// </summary>
    public Task ExtractAsync(IEnumerable<int> indexes, string destination, string stripPrefix = "",
        OverwriteMode overwrite = OverwriteMode.Overwrite,
        IProgress<ArchiveProgress>? progress = null, CancellationToken ct = default)
    {
        var set = indexes.ToHashSet();
        return Task.Run(() => ExtractCore(set, destination, stripPrefix, overwrite, progress, ct), ct);
    }

    /// <summary>Verifies every item against its checksum. Returns false when the archive is damaged.</summary>
    public Task<bool> TestAsync(IProgress<ArchiveProgress>? progress = null, CancellationToken ct = default) =>
        Task.Run(() =>
        {
            RequirePasswordIfEncrypted(null);
            try
            {
                using var handle = CreateExtractor();
                handle.Extractor.Extracting += (_, e) => progress?.Report(new ArchiveProgress(e.PercentDone, null));
                handle.Extractor.FileExtractionStarted += (_, e) =>
                {
                    if (ct.IsCancellationRequested) e.Cancel = true;
                };
                var ok = handle.Extractor.Check();
                ct.ThrowIfCancellationRequested();
                return ok;
            }
            catch (Exception ex) when (ex is not ArchiveException and not OperationCanceledException)
            {
                return false;
            }
        }, ct);

    private void ExtractCore(HashSet<int>? indexes, string destination, string stripPrefix,
        OverwriteMode overwrite, IProgress<ArchiveProgress>? progress, CancellationToken ct)
    {
        RequirePasswordIfEncrypted(indexes);

        var root = Path.GetFullPath(destination);
        Directory.CreateDirectory(root);
        var prefix = PathSafety.NormalizeEntryPath(stripPrefix);
        var failures = new List<string>();

        // Everything is unpacked into a staging folder inside the destination (same drive, so the
        // final step is a cheap move), and only then moved to a validated target path. A failed,
        // cancelled or wrong-password run therefore leaves nothing half-written in the destination.
        var stage = Path.Combine(root, ".stratum-" + Guid.NewGuid().ToString("N")[..8]);
        var plan = new List<(ArchiveEntry Entry, string Target, string Staged, bool Bulk)>();
        var directories = new List<string>();

        foreach (var entry in Entries)
        {
            if (indexes is not null && !indexes.Contains(entry.Index)) continue;

            var relative = entry.NormalizedPath;
            if (prefix.Length > 0)
            {
                if (!relative.StartsWith(prefix + "/", StringComparison.OrdinalIgnoreCase)) continue;
                relative = relative[(prefix.Length + 1)..];
            }

            var target = PathSafety.ResolveTarget(root, relative);
            if (target is null)
            {
                failures.Add($"{entry.Path}: skipped, the path points outside the destination folder.");
                continue;
            }
            if (entry.IsDirectory) { directories.Add(target); continue; }
            if (overwrite == OverwriteMode.Skip && File.Exists(target)) continue;

            // Names the engine can write as-is go through one fast bulk pass. Anything that needed
            // sanitising is unpacked one by one to a neutral file name instead.
            var natural = Path.GetFullPath(Path.Combine(stage, entry.NormalizedPath.Replace('/', '\\')));
            var bulk = string.Equals(PathSafety.ResolveTarget(stage, entry.Path), natural, StringComparison.OrdinalIgnoreCase);
            plan.Add((entry, target, bulk ? natural : Path.Combine(stage, ".odd", entry.Index.ToString()), bulk));
        }

        try
        {
            foreach (var dir in directories) Directory.CreateDirectory(dir);

            if (plan.Count > 0)
            {
                var info = Directory.CreateDirectory(stage);
                info.Attributes |= FileAttributes.Hidden;

                try
                {
                    using var handle = CreateExtractor();
                    var extractor = handle.Extractor;
                    extractor.Extracting += (_, e) => progress?.Report(new ArchiveProgress(Math.Min(e.PercentDone, (byte)99), null));
                    extractor.FileExtractionStarted += (_, e) =>
                    {
                        if (ct.IsCancellationRequested) e.Cancel = true;
                        progress?.Report(new ArchiveProgress(Math.Min(e.PercentDone, (byte)99), e.FileInfo.FileName));
                    };

                    var bulkIndexes = plan.Where(p => p.Bulk).Select(p => p.Entry.Index).ToArray();
                    if (bulkIndexes.Length == Entries.Count) extractor.ExtractArchive(stage);
                    else if (bulkIndexes.Length > 0) extractor.ExtractFiles(stage, bulkIndexes);

                    foreach (var odd in plan.Where(p => !p.Bulk))
                    {
                        ct.ThrowIfCancellationRequested();
                        Directory.CreateDirectory(Path.GetDirectoryName(odd.Staged)!);
                        using var output = File.Create(odd.Staged);
                        extractor.ExtractFile(odd.Entry.Index, output);
                    }
                }
                catch (Exception ex) when (ex is not ArchiveException and not OperationCanceledException)
                {
                    ct.ThrowIfCancellationRequested();
                    throw TranslateExtractFailure(ex);
                }

                ct.ThrowIfCancellationRequested();

                var moved = 0;
                foreach (var (entry, planned, staged, _) in plan)
                {
                    if (!File.Exists(staged))
                    {
                        failures.Add($"{entry.Path}: the item could not be extracted.");
                        continue;
                    }

                    var target = planned;
                    if (File.Exists(target))
                    {
                        if (overwrite == OverwriteMode.Skip) continue;
                        if (overwrite == OverwriteMode.Rename) target = PathSafety.MakeUnique(target);
                    }

                    try
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                        File.Move(staged, target, overwrite: true);
                        if (entry.Modified is { } time) File.SetLastWriteTime(target, time);
                        moved++;
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        failures.Add($"{entry.Path}: {ex.Message}");
                    }
                }

                if (moved == 0 && failures.Count > 0 && plan.Any(p => p.Entry.Encrypted)) throw new WrongPasswordException();
            }
        }
        finally
        {
            try { if (Directory.Exists(stage)) Directory.Delete(stage, true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        if (failures.Count > 0)
        {
            var shown = string.Join(Environment.NewLine, failures.Take(10));
            var more = failures.Count > 10 ? $"{Environment.NewLine}...and {failures.Count - 10} more." : "";
            throw new ArchiveException($"Some items could not be extracted:{Environment.NewLine}{shown}{more}");
        }

        progress?.Report(new ArchiveProgress(100, null));
    }

    private void RequirePasswordIfEncrypted(HashSet<int>? indexes)
    {
        if (_password is not null) return;
        if (Entries.Any(e => e.Encrypted && (indexes is null || indexes.Contains(e.Index))))
            throw new PasswordRequiredException();
    }

    private Exception TranslateOpenFailure(Exception ex)
    {
        if (_password is not null) return new WrongPasswordException(ex);
        return LooksEncryptable() ? new PasswordRequiredException()
            : new ArchiveException("This file is not a supported archive, or it is damaged.", ex);
    }

    private Exception TranslateExtractFailure(Exception ex) =>
        HasEncryptedEntries ? new WrongPasswordException(ex) : new ArchiveException(ex.Message, ex);

    /// <summary>True when the file starts with a 7z or RAR signature, the formats that can hide their file list.</summary>
    private bool LooksEncryptable()
    {
        try
        {
            Span<byte> head = stackalloc byte[6];
            using var s = File.OpenRead(ArchivePath);
            if (s.Read(head) < 6) return false;
            ReadOnlySpan<byte> sevenZip = [0x37, 0x7A, 0xBC, 0xAF, 0x27, 0x1C];
            ReadOnlySpan<byte> rar = [0x52, 0x61, 0x72, 0x21, 0x1A, 0x07];
            return head.SequenceEqual(sevenZip) || head.SequenceEqual(rar);
        }
        catch (IOException)
        {
            return false;
        }
    }

    private static T? SafeGet<T>(Func<T> get)
    {
        try { return get(); } catch (Exception) { return default; }
    }

    private ExtractorHandle CreateExtractor()
    {
        SevenZipLibrary.EnsureLoaded();
        if (SplitVolumeStream.IsFirstVolume(ArchivePath))
        {
            var stream = new SplitVolumeStream(ArchivePath);
            try
            {
                var ex = _password is null ? new SevenZipExtractor(stream) : new SevenZipExtractor(stream, _password);
                return new ExtractorHandle(ex, stream);
            }
            catch
            {
                stream.Dispose();
                throw;
            }
        }
        return new ExtractorHandle(
            _password is null ? new SevenZipExtractor(ArchivePath) : new SevenZipExtractor(ArchivePath, _password), null);
    }

    private sealed class ExtractorHandle(SevenZipExtractor extractor, SplitVolumeStream? volumes) : IDisposable
    {
        public SevenZipExtractor Extractor { get; } = extractor;
        public SplitVolumeStream? Volumes { get; } = volumes;

        public void Dispose()
        {
            Extractor.Dispose();
            Volumes?.Dispose();
        }
    }
}
