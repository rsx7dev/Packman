using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;

namespace Packman.Helpers;

public static class IconExtractor
{
    private const string TempFolderName = "Packman";

    [DllImport("shell32.dll", CharSet = CharSet.Auto)]
    private static extern uint ExtractIconEx(string lpszFile, int nIconIndex, IntPtr[]? phiconLarge, IntPtr[]? phiconSmall, uint nIcons);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr hIcon);

    public static string? ExtractIconToTemp(string sourceFilePath)
    {
        if (string.IsNullOrEmpty(sourceFilePath) || !File.Exists(sourceFilePath))
            return null;

        var hIcons = new IntPtr[1];
        try
        {
            if (ExtractIconEx(sourceFilePath, 0, hIcons, null, 1) == 0 || hIcons[0] == IntPtr.Zero)
                return null;

            var source = Imaging.CreateBitmapSourceFromHIcon(
                hIcons[0], Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());

            var tempPath = Path.Combine(Path.GetTempPath(), TempFolderName);
            Directory.CreateDirectory(tempPath);

            var iconPath = Path.Combine(tempPath, $"{Path.GetFileNameWithoutExtension(sourceFilePath)}_icon.png");
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(source));
            using (var fs = new FileStream(iconPath, FileMode.Create))
                encoder.Save(fs);

            return iconPath;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Icon extraction failed: {ex.Message}");
            return null;
        }
        finally
        {
            if (hIcons[0] != IntPtr.Zero)
                DestroyIcon(hIcons[0]);
        }
    }

    public static bool CopyIconToPackage(string extractedIconPath, string packageFolderPath, string appName)
    {
        if (string.IsNullOrEmpty(extractedIconPath) || !File.Exists(extractedIconPath)) return false;
        if (string.IsNullOrEmpty(packageFolderPath) || !Directory.Exists(packageFolderPath)) return false;

        try
        {
            var iconFolder = PsadtLayout.IconFolder(packageFolderPath);
            if (!Directory.Exists(iconFolder)) return false;

            var sanitized = SanitizeFileName(appName);
            File.Copy(extractedIconPath, Path.Combine(iconFolder, $"{sanitized}_icon.png"), overwrite: true);
            return true;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Icon copy failed: {ex.Message}");
            return false;
        }
    }

    private static string SanitizeFileName(string name)
    {
        if (string.IsNullOrEmpty(name)) return "app";
        var invalid = Path.GetInvalidFileNameChars();
        var sanitized = string.Join("_", name.Split(invalid, StringSplitOptions.RemoveEmptyEntries));
        return string.IsNullOrEmpty(sanitized) ? "app" : sanitized;
    }
}
