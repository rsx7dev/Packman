using System.IO;
using System.Text;

namespace Packman.Helpers;

public static class ProcessOutput
{
    /// <summary>
    /// Reads a child's output stream to its end, handing each non-blank line to
    /// <paramref name="onLine"/> as it arrives rather than when the process exits.
    /// Returns everything read.
    /// </summary>
    public static async Task<string> ReadLinesAsync(TextReader reader, Action<string> onLine, CancellationToken ct)
    {
        var all = new StringBuilder();
        while (await reader.ReadLineAsync(ct) is { } line)
        {
            all.AppendLine(line);
            if (!string.IsNullOrWhiteSpace(line)) onLine(line.Trim());
        }
        return all.ToString();
    }
}
