using System.IO;
using System.Text;

namespace Packman.Helpers;

/// <summary>
/// Reads and writes script files without changing their encoding. PSADT runs under
/// Windows PowerShell 5.1, which reads a BOM-less file as ANSI.
/// </summary>
public static class TextFileIO
{
    /// <summary>Extension of the temp file a save writes before swapping it in.</summary>
    public const string TempSuffix = ".packman.tmp";

    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    public sealed record TextFile(string Content, Encoding Encoding, bool Crlf);

    public static TextFile Read(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);

        var bytes = File.ReadAllBytes(path);
        var encoding = DetectEncoding(bytes, out var preambleLength);
        var content = encoding.GetString(bytes, preambleLength, bytes.Length - preambleLength);
        return new TextFile(content, encoding, content.Contains("\r\n") || !content.Contains('\n'));
    }

    /// <summary>Writes via a temp file so a failed write can't truncate the original.</summary>
    public static void Write(string path, string content, Encoding encoding)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(encoding);

        var temp = path + TempSuffix;
        File.WriteAllText(temp, content, encoding);

        try
        {
            if (File.Exists(path))
                File.Replace(temp, path, null);
            else
                File.Move(temp, path);
        }
        catch (IOException)
        {
            File.Move(temp, path, overwrite: true);
        }
    }

    private static Encoding DetectEncoding(byte[] bytes, out int preambleLength)
    {
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
        {
            preambleLength = 3;
            return new UTF8Encoding(encoderShouldEmitUTF8Identifier: true);
        }
        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
        {
            preambleLength = 2;
            return new UnicodeEncoding(bigEndian: false, byteOrderMark: true);
        }
        if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
        {
            preambleLength = 2;
            return new UnicodeEncoding(bigEndian: true, byteOrderMark: true);
        }

        preambleLength = 0;
        return Utf8NoBom;
    }
}
