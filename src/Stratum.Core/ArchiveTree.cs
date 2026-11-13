namespace Stratum.Core;

/// <summary>One row in the browser: a file, or a folder (which may exist only implicitly in entry paths).</summary>
public sealed record TreeItem(string Name, string FullPath, bool IsDirectory, long Size, DateTime? Modified, bool Encrypted, int? EntryIndex)
{
    /// <summary>CRC-32 of the file, or 0 when the format does not store one.</summary>
    public uint Crc { get; init; }
}

/// <summary>Folder view over the flat entry list of an archive.</summary>
public sealed class ArchiveTree
{
    private readonly IReadOnlyList<ArchiveEntry> _entries;

    public ArchiveTree(IReadOnlyList<ArchiveEntry> entries) => _entries = entries;

    /// <summary>Items directly inside <paramref name="folder"/> ("" is the root), folders first.</summary>
    public IReadOnlyList<TreeItem> List(string folder)
    {
        folder = PathSafety.NormalizeEntryPath(folder);
        var prefix = folder.Length == 0 ? "" : folder + "/";
        var folders = new Dictionary<string, TreeItem>(StringComparer.OrdinalIgnoreCase);
        var files = new List<TreeItem>();

        foreach (var e in _entries)
        {
            var path = e.NormalizedPath;
            if (path.Length <= prefix.Length || !path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;

            var rest = path[prefix.Length..];
            var slash = rest.IndexOf('/');
            if (slash < 0 && !e.IsDirectory)
            {
                files.Add(new TreeItem(rest, path, false, e.Size, e.Modified, e.Encrypted, e.Index) { Crc = e.Crc });
                continue;
            }

            var name = slash < 0 ? rest : rest[..slash];
            var isOwnEntry = slash < 0;
            folders.TryGetValue(name, out var existing);
            var size = (existing?.Size ?? 0) + (e.IsDirectory ? 0 : e.Size);
            folders[name] = new TreeItem(name, prefix + name, true, size,
                isOwnEntry ? e.Modified : existing?.Modified, false,
                isOwnEntry ? e.Index : existing?.EntryIndex);
        }

        return [.. folders.Values.OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase),
                .. files.OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase)];
    }

    /// <summary>Entry indexes covered by the given items: files themselves, folders with everything below them.</summary>
    public IReadOnlyList<int> IndexesUnder(IEnumerable<TreeItem> items)
    {
        var result = new HashSet<int>();
        foreach (var item in items)
        {
            if (!item.IsDirectory)
            {
                if (item.EntryIndex is { } i) result.Add(i);
                continue;
            }
            var prefix = item.FullPath + "/";
            foreach (var e in _entries)
            {
                var path = e.NormalizedPath;
                if (path.Equals(item.FullPath, StringComparison.OrdinalIgnoreCase) ||
                    path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    result.Add(e.Index);
            }
        }
        return [.. result];
    }

    public bool FolderExists(string folder)
    {
        folder = PathSafety.NormalizeEntryPath(folder);
        if (folder.Length == 0) return true;
        var prefix = folder + "/";
        return _entries.Any(e => e.NormalizedPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ||
                                 (e.IsDirectory && e.NormalizedPath.Equals(folder, StringComparison.OrdinalIgnoreCase)));
    }
}
