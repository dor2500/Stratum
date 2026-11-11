namespace Stratum.Core;

/// <summary>Keeps extracted files inside the destination folder ("zip slip" protection).</summary>
public static class PathSafety
{
    private static readonly char[] Separators = ['/', '\\'];

    public static string NormalizeEntryPath(string path) =>
        string.Join('/', path.Split(Separators, StringSplitOptions.RemoveEmptyEntries));

    /// <summary>
    /// Maps an archive entry path to a path under <paramref name="destinationRoot"/>.
    /// Returns null when the entry would land outside the destination or has no usable name.
    /// </summary>
    public static string? ResolveTarget(string destinationRoot, string entryPath)
    {
        var parts = entryPath.Split(Separators, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return null;

        var clean = new List<string>(parts.Length);
        foreach (var raw in parts)
        {
            if (raw == ".") continue;
            if (raw == "..") return null;

            // Drive letters ("C:") and alternate data streams have no place in a relative path.
            var part = raw.Replace(':', '_').TrimEnd(' ', '.');
            foreach (var c in Path.GetInvalidFileNameChars()) part = part.Replace(c, '_');
            if (part.Length == 0) return null;
            clean.Add(part);
        }
        if (clean.Count == 0) return null;

        var root = Path.GetFullPath(destinationRoot);
        var target = Path.GetFullPath(Path.Combine(root, Path.Combine([.. clean])));
        var rootWithSeparator = root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar;
        return target.StartsWith(rootWithSeparator, StringComparison.OrdinalIgnoreCase) ? target : null;
    }

    /// <summary>Returns "name (2).ext", "name (3).ext", ... until the path is free.</summary>
    public static string MakeUnique(string path)
    {
        if (!File.Exists(path) && !Directory.Exists(path)) return path;
        var dir = Path.GetDirectoryName(path) ?? "";
        var name = Path.GetFileNameWithoutExtension(path);
        var ext = Path.GetExtension(path);
        for (var i = 2; ; i++)
        {
            var candidate = Path.Combine(dir, $"{name} ({i}){ext}");
            if (!File.Exists(candidate) && !Directory.Exists(candidate)) return candidate;
        }
    }
}
