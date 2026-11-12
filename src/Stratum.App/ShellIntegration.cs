using Microsoft.Win32;

namespace Stratum.Shell;

/// <summary>
/// The classic Explorer context menu, registered per user (HKCU, no admin rights needed).
/// On Windows 11 these entries appear under "Show more options"; the top-level modern menu
/// is provided by the packaged shell extension in src/Stratum.ShellExt.
/// </summary>
internal static class ShellIntegration
{
    private const string FilesKey = @"Software\Classes\*\shell\Stratum";
    private const string FoldersKey = @"Software\Classes\Directory\shell\Stratum";

    private static string Exe => Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "Stratum.exe");

    public static bool IsRegistered
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(FilesKey);
            return key is not null;
        }
    }

    public static void Register()
    {
        Unregister();

        using (var root = Registry.CurrentUser.CreateSubKey(FilesKey))
        {
            root.SetValue("MUIVerb", "Stratum");
            root.SetValue("Icon", $"\"{Exe}\"");
            root.SetValue("SubCommands", "");
            AddVerb(root, "1open", "Open with Stratum", "--open");
            AddVerb(root, "2here", "Extract here", "--extract-here");
            AddVerb(root, "3to", "Extract to a new folder", "--extract-to");
            AddVerb(root, "4add", "Add to archive…", "--add");
        }

        using (var root = Registry.CurrentUser.CreateSubKey(FoldersKey))
        {
            root.SetValue("MUIVerb", "Add to archive with Stratum…");
            root.SetValue("Icon", $"\"{Exe}\"");
            using var command = root.CreateSubKey("command");
            command.SetValue("", $"\"{Exe}\" --add \"%1\"");
        }
    }

    public static void Unregister()
    {
        Registry.CurrentUser.DeleteSubKeyTree(FilesKey, throwOnMissingSubKey: false);
        Registry.CurrentUser.DeleteSubKeyTree(FoldersKey, throwOnMissingSubKey: false);
    }

    private static void AddVerb(RegistryKey root, string id, string label, string argument)
    {
        using var verb = root.CreateSubKey($@"shell\{id}");
        verb.SetValue("MUIVerb", label);
        using var command = verb.CreateSubKey("command");
        command.SetValue("", $"\"{Exe}\" {argument} \"%1\"");
    }
}
