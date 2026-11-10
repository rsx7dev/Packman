using System.IO;

namespace Packman.Helpers;

/// <summary>
/// The PSADT v4 template that ships next to Packman.exe, and where Packman keeps the
/// IntuneWinAppUtil.exe it downloads from Microsoft on the first publish.
/// </summary>
public static class BundledTools
{
    public static readonly string PsadtTemplate =
        Path.Combine(AppContext.BaseDirectory, "PSADT", PsadtLayout.ApplicationFolderName);

    public static readonly string IntuneWinAppUtil =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Packman", "Tools", "IntuneWinAppUtil-1.8.7", "IntuneWinAppUtil.exe");
}
