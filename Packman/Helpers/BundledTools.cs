using System.IO;

namespace Packman.Helpers;

/// <summary>The PSADT v4 template and IntuneWinAppUtil.exe that ship next to Packman.exe.</summary>
public static class BundledTools
{
    public static readonly string PsadtTemplate =
        Path.Combine(AppContext.BaseDirectory, "PSADT", PsadtLayout.ApplicationFolderName);

    public static readonly string IntuneWinAppUtil =
        Path.Combine(AppContext.BaseDirectory, "IntuneWinAppUtil", "IntuneWinAppUtil.exe");
}
