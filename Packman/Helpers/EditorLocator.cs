using System.Diagnostics;
using System.IO;

namespace Packman.Helpers;

/// <summary>Finds a script editor on the machine and opens files in it.</summary>
public static class EditorLocator
{
    public static string? FindVSCodePath()
    {
        string[] paths =
        [
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), @"Microsoft VS Code\Code.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), @"Microsoft VS Code\Code.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"Programs\Microsoft VS Code\Code.exe"),
        ];

        return paths.FirstOrDefault(File.Exists);
    }

    public static string? FindPowerShellISEPath()
    {
        var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), @"WindowsPowerShell\v1.0\PowerShell_ISE.exe");
        return File.Exists(path) ? path : null;
    }

    /// <summary>Opens a script in VS Code, else PowerShell ISE, else whatever the shell associates with it.</summary>
    public static void Open(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;

        var editor = FindVSCodePath() ?? FindPowerShellISEPath();
        var startInfo = editor != null
            ? new ProcessStartInfo(editor) { UseShellExecute = true, ArgumentList = { path } }
            : new ProcessStartInfo(path) { UseShellExecute = true };
        Process.Start(startInfo);
    }
}
