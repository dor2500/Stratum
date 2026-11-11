using SevenZip;

namespace Stratum.Core;

/// <summary>Locates and loads the native 7-Zip engine once per process.</summary>
public static class SevenZipLibrary
{
    private static readonly object Gate = new();
    private static bool _loaded;

    public static string? LoadedFrom { get; private set; }

    public static void EnsureLoaded()
    {
        if (_loaded) return;
        lock (Gate)
        {
            if (_loaded) return;
            var path = Candidates().FirstOrDefault(File.Exists)
                ?? throw new ArchiveException("The 7-Zip engine (7z.dll) was not found next to the application.");
            SevenZipBase.SetLibraryPath(path);
            LoadedFrom = path;
            _loaded = true;
        }
    }

    private static IEnumerable<string> Candidates()
    {
        yield return Path.Combine(AppContext.BaseDirectory, "7z.dll");
        yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "7-Zip", "7z.dll");
    }
}
