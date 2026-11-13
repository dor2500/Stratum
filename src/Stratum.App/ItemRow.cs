using Microsoft.UI.Xaml;
using Stratum.Core;

namespace Stratum.Shell;

public enum RowKind
{
    /// <summary>The ".." row that goes up one level.</summary>
    Parent,
    ArchiveItem,
    Folder,
    File,
    Drive,
}

/// <summary>One row of the file list: an item inside an archive, or a file, folder or drive on disk.</summary>
public sealed class ItemRow
{
    private ItemRow() { }

    public RowKind Kind { get; private init; }
    public TreeItem? Item { get; private init; }

    /// <summary>Full path on disk, for <see cref="RowKind.Folder"/>, <see cref="RowKind.File"/> and <see cref="RowKind.Drive"/>.</summary>
    public string? DiskPath { get; private init; }

    public string Name { get; private init; } = "";
    public bool IsDirectory { get; private init; }
    public long Size { get; private init; }
    public DateTime? Modified { get; private init; }
    public uint Crc { get; private init; }

    public string SizeText { get; private init; } = "";
    public string ModifiedText => Modified?.ToString("g") ?? "";
    public string TypeText { get; private init; } = "";
    public string CrcText => Crc != 0 ? Crc.ToString("X8") : "";
    public string Glyph { get; private init; } = "";
    public Visibility LockVisibility { get; private init; } = Visibility.Collapsed;
    public double IconOpacity => IsDirectory ? 1.0 : 0.75;

    public static ItemRow Parent() => new()
    {
        Kind = RowKind.Parent, Name = "..", IsDirectory = true, Glyph = "", TypeText = "",
    };

    public static ItemRow FromArchive(TreeItem item) => new()
    {
        Kind = RowKind.ArchiveItem,
        Item = item,
        Name = item.Name,
        IsDirectory = item.IsDirectory,
        Size = item.Size,
        Modified = item.Modified,
        Crc = item.Crc,
        SizeText = item.IsDirectory ? "" : item.Size.ToString("N0"),
        TypeText = item.IsDirectory ? "File folder" : Format.TypeName(item.Name),
        Glyph = item.IsDirectory ? "" : Format.Glyph(item.Name),
        LockVisibility = item.Encrypted ? Visibility.Visible : Visibility.Collapsed,
    };

    public static ItemRow FromFolder(DirectoryInfo info) => new()
    {
        Kind = RowKind.Folder, DiskPath = info.FullName, Name = info.Name, IsDirectory = true,
        Modified = info.LastWriteTime, TypeText = "File folder", Glyph = "",
    };

    public static ItemRow FromFile(FileInfo info) => new()
    {
        Kind = RowKind.File, DiskPath = info.FullName, Name = info.Name, Size = info.Length,
        Modified = info.LastWriteTime, SizeText = info.Length.ToString("N0"),
        TypeText = Format.TypeName(info.Name), Glyph = Format.Glyph(info.Name),
    };

    public static ItemRow FromDrive(DriveInfo drive)
    {
        var label = "";
        long size = 0;
        try
        {
            if (drive.IsReady)
            {
                label = drive.VolumeLabel;
                size = drive.TotalSize;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }

        var letter = drive.Name.TrimEnd('\\');
        return new ItemRow
        {
            Kind = RowKind.Drive, DiskPath = drive.Name, IsDirectory = true, Size = size,
            Name = label.Length > 0 ? $"{label} ({letter})" : letter,
            SizeText = size > 0 ? Format.Size(size) : "",
            TypeText = drive.DriveType switch
            {
                DriveType.Removable => "Removable disk",
                DriveType.Network => "Network drive",
                DriveType.CDRom => "CD drive",
                _ => "Local disk",
            },
            Glyph = "",
        };
    }
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
        if (ext.Length <= 1) return "File";
        return ArchiveExtensions.Contains(ext) ? $"{ext[1..].ToUpperInvariant()} archive" : $"{ext[1..].ToUpperInvariant()} file";
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
