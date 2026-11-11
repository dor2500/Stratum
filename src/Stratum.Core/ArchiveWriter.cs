using SevenZip;

namespace Stratum.Core;

/// <summary>Write-side operations: create an archive, add to one, delete from one.</summary>
public static class ArchiveWriter
{
    /// <summary>
    /// Creates <paramref name="archivePath"/> from files and folders. Folders are added recursively
    /// under their own name. Returns the files written (several when split into volumes).
    /// </summary>
    public static Task<IReadOnlyList<string>> CreateAsync(string archivePath, IEnumerable<string> sources,
        CreateOptions options, IProgress<ArchiveProgress>? progress = null, CancellationToken ct = default)
    {
        var list = sources.ToList();
        return Task.Run(() => Create(Path.GetFullPath(archivePath), list, options, progress, ct), ct);
    }

    /// <summary>Adds files and folders to an existing archive, under <paramref name="folderInArchive"/>.</summary>
    public static Task AddAsync(string archivePath, IEnumerable<string> sources, string folderInArchive = "",
        string? password = null, IProgress<ArchiveProgress>? progress = null, CancellationToken ct = default)
    {
        var list = sources.ToList();
        return Task.Run(() =>
        {
            RejectSplit(archivePath);
            var map = BuildFileMap(list, folderInArchive);
            if (map.Count == 0) throw new ArchiveException("There is nothing to add.");

            var compressor = NewCompressor(FormatOf(archivePath), CompressionStrength.Normal);
            compressor.CompressionMode = CompressionMode.Append;
            Run(compressor, archivePath, map, password, progress, ct, deleteOnFailure: false);
        }, ct);
    }

    /// <summary>Removes entries (by index) from an existing archive.</summary>
    public static Task DeleteAsync(string archivePath, IEnumerable<int> indexes, string? password = null,
        CancellationToken ct = default)
    {
        var map = indexes.Distinct().ToDictionary(i => i, _ => (string)null!);
        return Task.Run(() =>
        {
            RejectSplit(archivePath);
            if (map.Count == 0) return;
            try
            {
                var compressor = NewCompressor(FormatOf(archivePath), CompressionStrength.Normal);
                if (string.IsNullOrEmpty(password)) compressor.ModifyArchive(archivePath, map);
                else compressor.ModifyArchive(archivePath, map, password);
            }
            catch (Exception ex) when (ex is not ArchiveException)
            {
                throw new ArchiveException($"The archive could not be modified: {ex.Message}", ex);
            }
        }, ct);
    }

    private static IReadOnlyList<string> Create(string archivePath, List<string> sources, CreateOptions options,
        IProgress<ArchiveProgress>? progress, CancellationToken ct)
    {
        var hasPassword = !string.IsNullOrEmpty(options.Password);
        if (hasPassword && options.Format == CreateFormat.Tar)
            throw new ArchiveException("TAR archives cannot be encrypted. Choose ZIP or 7Z to use a password.");

        var map = BuildFileMap(sources, "");
        // Never pack the archive into itself.
        foreach (var key in map.Where(kv => PathEquals(kv.Value, archivePath)).Select(kv => kv.Key).ToList()) map.Remove(key);
        if (map.Count == 0) throw new ArchiveException("There is nothing to compress.");

        Directory.CreateDirectory(Path.GetDirectoryName(archivePath)!);
        if (File.Exists(archivePath)) File.Delete(archivePath);

        var compressor = NewCompressor(options.Format, options.Strength);
        if (hasPassword)
        {
            if (options.Format == CreateFormat.Zip) compressor.ZipEncryptionMethod = ZipEncryptionMethod.Aes256;
            if (options.Format == CreateFormat.SevenZip) compressor.EncryptHeaders = options.EncryptFileNames;
        }

        Run(compressor, archivePath, map, options.Password, progress, ct, deleteOnFailure: true);

        if (options.VolumeSize > 0 && new FileInfo(archivePath).Length > options.VolumeSize)
        {
            try
            {
                return VolumeSplitter.Split(archivePath, options.VolumeSize, ct);
            }
            catch
            {
                VolumeSplitter.TryDelete(archivePath);
                throw;
            }
        }
        return [archivePath];
    }

    private static void Run(SevenZipCompressor compressor, string archivePath, Dictionary<string, string> map,
        string? password, IProgress<ArchiveProgress>? progress, CancellationToken ct, bool deleteOnFailure)
    {
        string? current = null;
        compressor.FileCompressionStarted += (_, e) =>
        {
            current = e.FileName;
            if (ct.IsCancellationRequested) e.Cancel = true;
            progress?.Report(new ArchiveProgress(e.PercentDone, current));
        };
        compressor.Compressing += (_, e) => progress?.Report(new ArchiveProgress(e.PercentDone, current));

        try
        {
            if (string.IsNullOrEmpty(password)) compressor.CompressFileDictionary(map, archivePath);
            else compressor.CompressFileDictionary(map, archivePath, password);
        }
        catch (Exception ex) when (ex is not ArchiveException and not OperationCanceledException)
        {
            if (deleteOnFailure) VolumeSplitter.TryDelete(archivePath);
            ct.ThrowIfCancellationRequested();
            throw new ArchiveException($"The archive could not be written: {ex.Message}", ex);
        }

        if (ct.IsCancellationRequested)
        {
            if (deleteOnFailure) VolumeSplitter.TryDelete(archivePath);
            ct.ThrowIfCancellationRequested();
        }
        progress?.Report(new ArchiveProgress(100, null));
    }

    private static SevenZipCompressor NewCompressor(CreateFormat format, CompressionStrength strength)
    {
        SevenZipLibrary.EnsureLoaded();
        return new SevenZipCompressor
        {
            ArchiveFormat = format switch
            {
                CreateFormat.Zip => OutArchiveFormat.Zip,
                CreateFormat.SevenZip => OutArchiveFormat.SevenZip,
                CreateFormat.Tar => OutArchiveFormat.Tar,
                _ => throw new ArgumentOutOfRangeException(nameof(format)),
            },
            CompressionLevel = strength switch
            {
                CompressionStrength.Store => CompressionLevel.None,
                CompressionStrength.Fast => CompressionLevel.Fast,
                CompressionStrength.Normal => CompressionLevel.Normal,
                CompressionStrength.Maximum => CompressionLevel.Ultra,
                _ => CompressionLevel.Normal,
            },
            CompressionMode = CompressionMode.Create,
            FastCompression = false,
        };
    }

    /// <summary>Maps "name inside the archive" to "file on disk" for every file under the sources.</summary>
    internal static Dictionary<string, string> BuildFileMap(IEnumerable<string> sources, string folderInArchive)
    {
        var prefix = PathSafety.NormalizeEntryPath(folderInArchive).Replace('/', '\\');
        if (prefix.Length > 0) prefix += "\\";

        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in sources)
        {
            var source = Path.GetFullPath(raw).TrimEnd('\\', '/');
            if (File.Exists(source))
            {
                map.TryAdd(prefix + Path.GetFileName(source), source);
            }
            else if (Directory.Exists(source))
            {
                var parent = Path.GetDirectoryName(source);
                var cut = parent is null ? source.Length : parent.TrimEnd('\\').Length + 1;
                var enumeration = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = 0 };
                foreach (var file in Directory.EnumerateFiles(source, "*", enumeration))
                    map.TryAdd(prefix + file[cut..], file);
            }
        }
        return map;
    }

    private static CreateFormat FormatOf(string archivePath) => Path.GetExtension(archivePath).ToLowerInvariant() switch
    {
        ".zip" => CreateFormat.Zip,
        ".7z" => CreateFormat.SevenZip,
        ".tar" => CreateFormat.Tar,
        _ => throw new ArchiveException("Only ZIP, 7Z and TAR archives can be modified."),
    };

    private static void RejectSplit(string archivePath)
    {
        if (SplitVolumeStream.IsFirstVolume(archivePath))
            throw new ArchiveException("Split archives cannot be modified.");
    }

    private static bool PathEquals(string a, string b) =>
        string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);
}
