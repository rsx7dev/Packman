using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace Packman.Helpers;

/// <summary>
/// One package's Invoke-AppDeployToolkit.ps1. Reads and edits the $adtSession hashtable,
/// the install/uninstall sections, the source-file references and the MSI product code
/// without disturbing the packager's formatting, encoding or line endings. The only
/// place that knows how the script is shaped, so create, upgrade and the readers cannot
/// drift apart.
/// </summary>
public sealed class PsadtScript
{
    public const string SignatureBegin = "# SIG # Begin signature block";
    public const string SignatureEnd = "# SIG # End signature block";

    /// <summary>Stands in for a product code the script does not know yet.</summary>
    public const string ProductCodePlaceholder = "{PRODUCT-CODE-PLACEHOLDER}";

    public const string InstallSection = "Installation";
    public const string UninstallSection = "Uninstallation";

    private static readonly Regex GuidPattern = new(
        @"\{[0-9A-Fa-f]{8}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{12}\}",
        RegexOptions.Compiled);

    // "$($adtSession.DirFiles)\setup.exe" as the generator writes it, plus the bare
    // "$adtSession.DirFiles\setup.exe" that hand-written scripts carry.
    private static readonly Regex DirFilesReference = new(
        @"(?<prefix>\$\(\$adtSession\.DirFiles\)\\|\$adtSession\.DirFiles\\)(?<name>[^""'\\\r\n]+?\.(?:exe|msi|bat|cmd|ps1))(?=[""'\s]|$)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex UninstallLine = new(
        @"Start-ADTMsiProcess[^\r\n]*-Action\s+'?Uninstall'?[^\r\n]*",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex SessionStart = new(@"^\$adtSession\s*=\s*@\{", RegexOptions.Compiled | RegexOptions.Multiline);
    private static readonly Regex BlockEnd = new(@"^\}", RegexOptions.Compiled | RegexOptions.Multiline);

    private readonly string? _path;
    private readonly Encoding _encoding;

    public string Content { get; private set; }

    /// <summary>The script's own line ending, used for anything inserted.</summary>
    public string NewLine { get; }

    private PsadtScript(string? path, string content, Encoding encoding, bool crlf)
    {
        _path = path;
        _encoding = encoding;
        Content = content;
        NewLine = crlf ? "\r\n" : "\n";
    }

    public static PsadtScript Load(string path)
    {
        var file = TextFileIO.Read(path);
        return new PsadtScript(path, file.Content, file.Encoding, file.Crlf);
    }

    /// <summary>In-memory script, for tests and previews. Saves as UTF-8 with BOM.</summary>
    public static PsadtScript Parse(string content) =>
        new(null, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true),
            content.Contains("\r\n") || !content.Contains('\n'));

    /// <summary>Writes back to the file it was loaded from, keeping its encoding.</summary>
    public void Save()
    {
        if (_path == null)
            throw new InvalidOperationException("This script was not loaded from a file; use SaveAs.");
        TextFileIO.Write(_path, Content, _encoding);
    }

    public void SaveAs(string path) => TextFileIO.Write(path, Content, _encoding);

    // ── $adtSession ───────────────────────────────────────────────────────────

    public string Vendor { get => GetSessionValue("AppVendor") ?? ""; set => SetSessionValue("AppVendor", value); }
    public string AppName { get => GetSessionValue("AppName") ?? ""; set => SetSessionValue("AppName", value); }
    public string AppVersion { get => GetSessionValue("AppVersion") ?? ""; set => SetSessionValue("AppVersion", value); }
    public string AppArch { get => GetSessionValue("AppArch") ?? ""; set => SetSessionValue("AppArch", value); }
    public string ScriptDate { get => GetSessionValue("AppScriptDate") ?? ""; set => SetSessionValue("AppScriptDate", value); }
    public string ScriptAuthor { get => GetSessionValue("AppScriptAuthor") ?? ""; set => SetSessionValue("AppScriptAuthor", value); }

    /// <summary>Defaults to true when the key is missing, as PSADT itself does.</summary>
    public bool RequireAdmin
    {
        get => !string.Equals(GetSessionValue("RequireAdmin"), "$false", StringComparison.OrdinalIgnoreCase);
        set => SetSessionBool("RequireAdmin", value);
    }

    /// <summary>"System" or "User", derived from RequireAdmin.</summary>
    public string InstallContext
    {
        get => RequireAdmin ? "System" : "User";
        set => RequireAdmin = !string.Equals(value, "User", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Today's date the way the template writes it, independent of the machine's culture.</summary>
    public static string TodayStamp => DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    /// <summary>Unquoted value of one $adtSession entry, or null when the key is absent.</summary>
    public string? GetSessionValue(string key)
    {
        var (start, length) = SessionBlock();
        var match = EntryPattern(key).Match(Content, start, length);
        return match.Success ? Unquote(match.Groups["value"].Value) : null;
    }

    /// <summary>Writes a single-quoted value. Returns false when the key is not in the hashtable.</summary>
    public bool SetSessionValue(string key, string? value)
        => SetSessionLiteral(key, "'" + PowerShellLiteral.SingleQuoted(value) + "'");

    public bool SetSessionBool(string key, bool value)
        => SetSessionLiteral(key, value ? "$true" : "$false");

    private bool SetSessionLiteral(string key, string literal)
    {
        var (start, length) = SessionBlock();
        var match = EntryPattern(key).Match(Content, start, length);
        if (!match.Success) return false;

        var value = match.Groups["value"];
        Content = string.Concat(Content.AsSpan(0, value.Index), literal, Content.AsSpan(value.Index + value.Length));
        return true;
    }

    // Span of the "$adtSession = @{ ... }" block; the whole file when it cannot be found.
    private (int start, int length) SessionBlock()
    {
        var open = SessionStart.Match(Content);
        if (!open.Success) return (0, Content.Length);

        var close = BlockEnd.Match(Content, open.Index);
        return close.Success ? (open.Index, close.Index - open.Index) : (open.Index, Content.Length - open.Index);
    }

    // A "Key = value" line. The value is a single-quoted string ('' escapes), a
    // double-quoted string (backtick escapes), a $variable or a bare token.
    private static Regex EntryPattern(string key) => new(
        $@"^[ \t]*{Regex.Escape(key)}[ \t]*=[ \t]*(?<value>'(?:[^']|'')*'|""(?:[^""`]|`.)*""|\$[A-Za-z_]\w*|[^\s#]+)",
        RegexOptions.Multiline);

    private static string Unquote(string literal)
    {
        if (literal.Length >= 2 && literal[0] == '\'' && literal[^1] == '\'')
            return literal[1..^1].Replace("''", "'")
                .Replace("\u2018\u2018", "\u2018")
                .Replace("\u2019\u2019", "\u2019")
                .Replace("\u201a\u201a", "\u201a")
                .Replace("\u201b\u201b", "\u201b");

        if (literal.Length >= 2 && literal[0] == '"' && literal[^1] == '"')
            return UnescapeDoubleQuoted(literal[1..^1]);

        return literal;
    }

    private static string UnescapeDoubleQuoted(string body)
    {
        var sb = new StringBuilder(body.Length);
        for (int i = 0; i < body.Length; i++)
        {
            if (body[i] != '`' || i + 1 >= body.Length) { sb.Append(body[i]); continue; }
            i++;
            sb.Append(body[i] switch
            {
                'n' => '\n',
                't' => '\t',
                'r' => '\r',
                '0' => '\0',
                _ => body[i],
            });
        }
        return sb.ToString();
    }

    // ── Signature block ───────────────────────────────────────────────────────

    public bool HasSignatureBlock => Content.Contains(SignatureBegin, StringComparison.Ordinal);

    /// <summary>
    /// Removes an Authenticode signature block. Any edit invalidates it, and an invalid
    /// signature is worse than none under an AllSigned policy.
    /// </summary>
    public bool StripSignatureBlock()
    {
        var begin = Content.IndexOf(SignatureBegin, StringComparison.Ordinal);
        if (begin < 0) return false;

        var end = Content.IndexOf(SignatureEnd, begin, StringComparison.Ordinal);
        var cut = end < 0 ? Content.Length : end + SignatureEnd.Length;

        var head = Content[..begin].TrimEnd('\r', '\n', ' ', '\t');
        var tail = Content[cut..].TrimStart('\r', '\n');
        Content = head + NewLine + tail;
        return true;
    }

    // ── Install / uninstall sections ──────────────────────────────────────────

    /// <summary>
    /// Inserts lines right after the "&lt;Perform {section} tasks here&gt;" marker.
    /// Returns false when the template has no such marker.
    /// </summary>
    public bool InsertAfterSection(string section, params string[] codeLines)
    {
        var lines = Content.Split('\n').ToList();   // '\r' stays on each line, so joining restores CRLF
        var index = FindSection(lines, section);
        if (index < 0) return false;

        var cr = NewLine == "\r\n" ? "\r" : "";
        for (int i = 0; i < codeLines.Length; i++)
            lines.Insert(index + i, codeLines[i] + cr);

        Content = string.Join('\n', lines);
        return true;
    }

    private static int FindSection(List<string> lines, string name)
    {
        for (int i = 0; i < lines.Count; i++)
        {
            if (lines[i].Contains($"<Perform {name} tasks here>") ||
                lines[i].Contains($"## {name}") ||
                lines[i].Contains($"## <{name}>"))
                return i + 1;
        }
        return -1;
    }

    /// <summary>
    /// Blank <paramref name="arguments"/> keeps the placeholder the packager must replace (EXE)
    /// or the plain line (MSI).
    /// </summary>
    public static string InstallCommand(bool msi, string sourceFileName, string arguments = "") => msi
        ? $"Start-ADTMsiProcess -Action 'Install' -FilePath \"$($adtSession.DirFiles)\\{PowerShellLiteral.DoubleQuoted(sourceFileName)}\"{MsiArguments(arguments)}"
        : $"Start-ADTProcess -FilePath \"$($adtSession.DirFiles)\\{PowerShellLiteral.DoubleQuoted(sourceFileName)}\" -ArgumentList '{ExeArguments(arguments, "<silent flags>")}'";

    public static string UninstallCommand(bool msi, string sourceFileName, string? productCode, string arguments = "") => msi
        ? $"Start-ADTMsiProcess -Action 'Uninstall' -FilePath '{PowerShellLiteral.SingleQuoted(string.IsNullOrWhiteSpace(productCode) ? ProductCodePlaceholder : productCode)}'{MsiArguments(arguments)}"
        : $"Start-ADTProcess -FilePath \"$($adtSession.DirFiles)\\{PowerShellLiteral.DoubleQuoted(sourceFileName)}\" -ArgumentList '{ExeArguments(arguments, "<uninstall flags>")}'";

    private static string ExeArguments(string arguments, string placeholder) =>
        string.IsNullOrWhiteSpace(arguments) ? placeholder : PowerShellLiteral.SingleQuoted(arguments.Trim());

    private static string MsiArguments(string arguments) =>
        string.IsNullOrWhiteSpace(arguments) ? "" : $" -AdditionalArgumentList '{PowerShellLiteral.SingleQuoted(arguments.Trim())}'";

    // ── Source file and product code ──────────────────────────────────────────

    /// <summary>First installer referenced through $adtSession.DirFiles, or null.</summary>
    public string? SourceFileName
    {
        get
        {
            var match = DirFilesReference.Match(Content);
            return match.Success ? match.Groups["name"].Value : null;
        }
    }

    /// <summary>Points every $adtSession.DirFiles reference at a new installer. Returns how many changed.</summary>
    public int ReplaceSourceFileName(string newSourceFileName)
    {
        var escaped = PowerShellLiteral.DoubleQuoted(newSourceFileName);
        int count = 0;
        Content = DirFilesReference.Replace(Content, m =>
        {
            count++;
            return m.Groups["prefix"].Value + escaped;
        });
        return count;
    }

    /// <summary>The product code on the MSI uninstall line, i.e. the package's own MSI. Null when there is none.</summary>
    public string? MsiProductCode
    {
        get
        {
            foreach (Match line in UninstallLine.Matches(Content))
            {
                var guid = GuidPattern.Match(line.Value);
                if (guid.Success) return guid.Value;
            }
            return null;
        }
    }

    /// <summary>
    /// Replaces one specific GUID (or the placeholder) everywhere it appears. Other GUIDs
    /// in the script, such as registry CLSIDs, are left alone. Returns how many changed.
    /// </summary>
    public int ReplaceProductCode(string? oldCode, string newCode)
    {
        if (string.IsNullOrWhiteSpace(oldCode) || string.IsNullOrWhiteSpace(newCode)) return 0;

        int count = 0;
        Content = Regex.Replace(Content, Regex.Escape(oldCode), _ =>
        {
            count++;
            return newCode;
        }, RegexOptions.IgnoreCase);
        return count;
    }

    /// <summary>Path of the script inside a package folder, or null when the folder holds none.</summary>
    public static string? Find(string packagePath)
    {
        if (string.IsNullOrEmpty(packagePath)) return null;

        foreach (var folder in new[] { PsadtLayout.ApplicationFolder(packagePath), packagePath })
        {
            var candidate = Path.Combine(folder, PsadtLayout.ScriptName);
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }
}
