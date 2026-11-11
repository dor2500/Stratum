namespace Stratum.Core;

/// <summary>Formats Stratum can create. RAR is extract-only by design (proprietary format).</summary>
public enum CreateFormat
{
    Zip,
    SevenZip,
    Tar,
}

public enum CompressionStrength
{
    Store,
    Fast,
    Normal,
    Maximum,
}

public enum OverwriteMode
{
    Overwrite,
    Skip,
    Rename,
}

/// <summary>One item stored in an archive.</summary>
public sealed record ArchiveEntry(
    int Index,
    string Path,
    bool IsDirectory,
    long Size,
    DateTime? Modified,
    bool Encrypted,
    uint Crc,
    string? Method)
{
    /// <summary>Path with forward slashes and no leading or trailing separator.</summary>
    public string NormalizedPath { get; } = PathSafety.NormalizeEntryPath(Path);
}

public sealed record CreateOptions
{
    public CreateFormat Format { get; init; } = CreateFormat.Zip;
    public CompressionStrength Strength { get; init; } = CompressionStrength.Normal;

    /// <summary>When set, contents are encrypted with AES-256.</summary>
    public string? Password { get; init; }

    /// <summary>7z only: also encrypt the file list.</summary>
    public bool EncryptFileNames { get; init; }

    /// <summary>When greater than zero, the archive is split into name.001, name.002, ... of this many bytes.</summary>
    public long VolumeSize { get; init; }

    public static string Extension(CreateFormat format) => format switch
    {
        CreateFormat.Zip => ".zip",
        CreateFormat.SevenZip => ".7z",
        CreateFormat.Tar => ".tar",
        _ => throw new ArgumentOutOfRangeException(nameof(format)),
    };
}

public readonly record struct ArchiveProgress(double Percent, string? CurrentItem);

public class ArchiveException : Exception
{
    public ArchiveException(string message, Exception? inner = null) : base(message, inner) { }
}

/// <summary>The archive (or the selected items) is encrypted and no password was supplied.</summary>
public sealed class PasswordRequiredException : ArchiveException
{
    public PasswordRequiredException() : base("This archive is password protected.") { }
}

public sealed class WrongPasswordException : ArchiveException
{
    public WrongPasswordException(Exception? inner = null) : base("The password is incorrect.", inner) { }
}
