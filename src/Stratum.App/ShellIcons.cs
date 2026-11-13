using System.Runtime.InteropServices;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;

namespace Stratum.Shell;

/// <summary>
/// The real Windows icon for a file type, folder or drive, as shown in Explorer.
/// Icons are looked up by extension (the file does not have to exist) and cached.
/// Call from the UI thread.
/// </summary>
internal static class ShellIcons
{
    private const uint SHGFI_ICON = 0x100;
    private const uint SHGFI_LARGEICON = 0x0;
    private const uint SHGFI_USEFILEATTRIBUTES = 0x10;
    private const uint FILE_ATTRIBUTE_DIRECTORY = 0x10;
    private const uint FILE_ATTRIBUTE_NORMAL = 0x80;

    private static readonly Dictionary<string, ImageSource?> Cache = new(StringComparer.OrdinalIgnoreCase);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHFILEINFO
    {
        public nint hIcon;
        public int iIcon;
        public uint dwAttributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szDisplayName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)] public string szTypeName;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern nint SHGetFileInfo(string path, uint attributes, ref SHFILEINFO info, uint size, uint flags);

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(nint icon);

    public static ImageSource? ForFolder() => Get("<folder>", "folder", FILE_ATTRIBUTE_DIRECTORY, true);

    public static ImageSource? ForFile(string name)
    {
        var ext = Path.GetExtension(name);
        return Get(ext.Length == 0 ? "<file>" : ext, ext.Length == 0 ? "file" : "file" + ext, FILE_ATTRIBUTE_NORMAL, true);
    }

    /// <summary>Drives have individual icons, so the real path is queried.</summary>
    public static ImageSource? ForDrive(string root) => Get(root, root, 0, false);

    private static ImageSource? Get(string key, string path, uint attributes, bool byAttributes)
    {
        if (Cache.TryGetValue(key, out var cached)) return cached;

        ImageSource? result = null;
        var info = new SHFILEINFO();
        var flags = SHGFI_ICON | SHGFI_LARGEICON | (byAttributes ? SHGFI_USEFILEATTRIBUTES : 0);
        if (SHGetFileInfo(path, attributes, ref info, (uint)Marshal.SizeOf<SHFILEINFO>(), flags) != 0 && info.hIcon != 0)
        {
            try
            {
                using var icon = System.Drawing.Icon.FromHandle(info.hIcon);
                using var bitmap = icon.ToBitmap();
                using var stream = new MemoryStream();
                bitmap.Save(stream, System.Drawing.Imaging.ImageFormat.Png);
                stream.Position = 0;
                var image = new BitmapImage();
                image.SetSource(stream.AsRandomAccessStream());
                result = image;
            }
            catch (Exception)
            {
                // Fall back to no icon; the row still shows its name.
            }
            finally
            {
                DestroyIcon(info.hIcon);
            }
        }

        Cache[key] = result;
        return result;
    }
}
