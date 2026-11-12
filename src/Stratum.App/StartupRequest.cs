namespace Stratum.Shell;

public enum StartupMode
{
    None,
    Open,
    ExtractHere,
    ExtractToFolder,
    Add,
}

/// <summary>
/// What the process was asked to do on the command line. This is the contract with the
/// Explorer context menu:
///   Stratum.exe archive.zip
///   Stratum.exe --extract-here archive.zip [more...]
///   Stratum.exe --extract-to archive.zip [more...]
///   Stratum.exe --add file-or-folder [more...]
/// </summary>
public sealed record StartupRequest(StartupMode Mode, IReadOnlyList<string> Paths)
{
    public static StartupRequest Parse(string[] args)
    {
        if (args.Length == 0) return new StartupRequest(StartupMode.None, []);

        var mode = args[0].ToLowerInvariant() switch
        {
            "--extract-here" => StartupMode.ExtractHere,
            "--extract-to" => StartupMode.ExtractToFolder,
            "--add" => StartupMode.Add,
            "--open" => StartupMode.Open,
            _ => StartupMode.None,
        };

        var rest = mode == StartupMode.None ? args : args[1..];
        var paths = rest.Where(a => !a.StartsWith("--", StringComparison.Ordinal))
                        .Select(Path.GetFullPath)
                        .Where(p => File.Exists(p) || Directory.Exists(p))
                        .ToList();

        if (paths.Count == 0) return new StartupRequest(StartupMode.None, []);
        return new StartupRequest(mode == StartupMode.None ? StartupMode.Open : mode, paths);
    }
}
