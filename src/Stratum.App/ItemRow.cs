using Microsoft.UI.Xaml;
using Stratum.Core;

namespace Stratum.Shell;

/// <summary>One row of the file list, pre-formatted for display.</summary>
public sealed class ItemRow
{
    public ItemRow(TreeItem item)
    {
        Item = item;
        Name = item.Name;
        SizeText = Format.Size(item.Size);
        ModifiedText = item.Modified?.ToString("g") ?? "";
        TypeText = item.IsDirectory ? "Folder" : Format.TypeName(item.Name);
        Glyph = item.IsDirectory ? "" : Format.Glyph(item.Name);
        LockVisibility = item.Encrypted ? Visibility.Visible : Visibility.Collapsed;
        IconOpacity = item.IsDirectory ? 1.0 : 0.75;
    }

    public TreeItem Item { get; }
    public string Name { get; }
    public string SizeText { get; }
    public string ModifiedText { get; }
    public string TypeText { get; }
    public string Glyph { get; }
    public Visibility LockVisibility { get; }
    public double IconOpacity { get; }
}

public static class Format
{
    private static readonly string[] Units = ["B", "KB", "MB", "GB", "TB"];

    public static readonly HashSet<string> ArchiveExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".zip", ".7z", ".rar", ".tar", ".gz", ".tgz", ".bz2", ".tbz2", ".xz", ".txz", ".001",
        ".cab", ".iso", ".wim", ".jar", ".apk", ".lzh", ".arj", ".z", ".zst",
    };

    private static readonly HashSet<string> Images = new(StringComparer.OrdinalIgnoreCase)
        { ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".webp", ".svg", ".ico", ".tif", ".tiff", ".heic" };
    private static readonly HashSet<string> Audio = new(StringComparer.OrdinalIgnoreCase)
        { ".mp3", ".wav", ".flac", ".ogg", ".m4a", ".aac", ".wma" };
    private static readonly HashSet<string> Video = new(StringComparer.OrdinalIgnoreCase)
        { ".mp4", ".mkv", ".avi", ".mov", ".webm", ".wmv" };

    /// <summary>File types that run code when opened; opening one from an archive asks first.</summary>
    public static readonly HashSet<string> Executables = new(StringComparer.OrdinalIgnoreCase)
    {
        ".exe", ".msi", ".bat", ".cmd", ".com", ".scr", ".ps1", ".vbs", ".js", ".jse", ".wsf",
        ".lnk", ".hta", ".reg", ".msix", ".appx", ".jar", ".pif", ".cpl",
    };

    public static string Size(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        double value = bytes;
        var unit = 0;
        while (value >= 1024 && unit < Units.Length - 1)
        {
            value /= 1024;
            unit++;
        }
        return $"{value:0.##} {Units[unit]}";
    }

    public static string TypeName(string name)
    {
        var ext = Path.GetExtension(name);
        return ext.Length > 1 ? $"{ext[1..].ToUpperInvariant()} file" : "File";
    }

    public static string Glyph(string name)
    {
        var ext = Path.GetExtension(name);
        if (ArchiveExtensions.Contains(ext)) return "";
        if (Images.Contains(ext)) return "";
        if (Audio.Contains(ext)) return "";
        if (Video.Contains(ext)) return "";
        if (Executables.Contains(ext)) return "";
        return "";
    }

    public static bool LooksLikeArchive(string path) =>
        File.Exists(path) && ArchiveExtensions.Contains(Path.GetExtension(path));
}
