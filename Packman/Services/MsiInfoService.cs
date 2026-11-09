using Packman.Helpers;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;

namespace Packman.Services;

public class MsiInfoService
{
    /// <summary>One row of the MSI Property table.</summary>
    public record MsiProperty(string Name, string Value)
    {
        /// <summary>Public properties (no lowercase letter) can be set on the msiexec command line.</summary>
        public bool IsPublic => IsPublicName(Name);

        /// <summary>The <c>NAME=value</c> text the install command takes.</summary>
        public string Argument => ArgumentLine.MsiProperty(Name, Value);
    }

    /// <summary>A name msiexec accepts on the command line: letters, none of them lowercase.</summary>
    public static bool IsPublicName(string name) => name.Any(char.IsLetter) && !name.Any(char.IsLower);

    /// <summary>One row of the Feature table. ADDLOCAL and REMOVE take <paramref name="Name"/>.</summary>
    /// <param name="Detail">The feature's title and whether it installs when no feature property is passed.</param>
    public record MsiFeature(string Name, string Detail);

    /// <summary>
    /// A public property one of the installer's own dialogs sets: an option the vendor offers,
    /// read from the Control table rather than guessed.
    /// </summary>
    /// <param name="Detail">The control, its label, the values it can set and the default.</param>
    /// <param name="Value">What the Add button passes: the checked value, else the default, else the first choice.</param>
    public record MsiOption(string Name, string Detail, string Value)
    {
        public string Argument => ArgumentLine.MsiProperty(Name, Value);
    }

    // Summary information stream property IDs.
    private const int SummaryTemplate = 7;
    private static readonly int[] SummaryIds = [SummaryTemplate];

    /// <summary>Properties the rail shows first, in this order, when the MSI has them.</summary>
    public static readonly string[] KeyPropertyNames =
    [
        "ProductName", "ProductVersion", "Manufacturer", "ProductCode", "UpgradeCode",
        "ALLUSERS", "REBOOT", "REBOOTPROMPT", "MSIRESTARTMANAGERCONTROL",
        "INSTALLDIR", "INSTALLFOLDER", "TARGETDIR", "APPDIR",
    ];

    public class MsiInfo
    {
        public string ProductCode { get; set; } = "";
        public string ProductName { get; set; } = "";
        public string ProductVersion { get; set; } = "";
        public string Manufacturer { get; set; } = "";
        public string UpgradeCode { get; set; } = "";
        public bool IsValid => !string.IsNullOrEmpty(ProductCode);

        /// <summary>The whole Property table, in table order.</summary>
        public IReadOnlyList<MsiProperty> Properties { get; set; } = [];

        /// <summary>File names of the .mst transforms beside the MSI.</summary>
        public IReadOnlyList<string> Transforms { get; set; } = [];

        /// <summary>The summary information stream by property ID; only the IDs that hold a value.</summary>
        public IReadOnlyDictionary<int, string> Summary { get; set; } = new Dictionary<int, string>();

        /// <summary>The Feature table, in table order.</summary>
        public IReadOnlyList<MsiFeature> Features { get; set; } = [];

        /// <summary>Public properties the installer's dialogs set.</summary>
        public IReadOnlyList<MsiOption> Options { get; set; } = [];

        /// <summary>
        /// Where the MSI disagrees with how the package is set up, one sentence each; empty when
        /// it all lines up. Platform and scope that match, and English or neutral language, are
        /// the normal case and say nothing.
        /// </summary>
        public IReadOnlyList<string> Warnings(bool userInstall, string architecture)
        {
            var warnings = new List<string>();
            if (Summary.TryGetValue(SummaryTemplate, out var template))
            {
                var platform = PlatformOf(template);
                if (!string.IsNullOrEmpty(architecture) && !platform.Equals(architecture, StringComparison.OrdinalIgnoreCase))
                    warnings.Add($"The MSI is built for {platform}, but the package is set to {architecture}.");

                var ids = template[(template.IndexOf(';') + 1)..]
                    .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                if (template.Contains(';') && ids.Any(id => id is not ("0" or "1033")))
                    warnings.Add($"MSI languages: {LanguagesOf(template)}.");
            }

            var scope = InstallScope;
            if (!userInstall && scope.StartsWith("Per user", StringComparison.Ordinal))
                warnings.Add($"Install scope is “{scope}”, but the package installs as System. Pass ALLUSERS=1 for a per-machine install.");
            else if (userInstall && scope == "Per machine")
                warnings.Add("The MSI installs per machine (ALLUSERS=1), which needs elevation, but the package installs as User.");
            return warnings;
        }

        /// <summary>Per machine or per user, as ALLUSERS in the Property table sets it; empty when the table was not read.</summary>
        public string InstallScope
        {
            get
            {
                if (Properties.Count == 0) return "";
                string Value(string name) => Properties.FirstOrDefault(p => p.Name == name)?.Value ?? "";
                return Value("ALLUSERS") switch
                {
                    "1" => "Per machine",
                    "2" => Value("MSIINSTALLPERUSER") == "1" ? "Per user" : "Per machine when elevated, otherwise per user",
                    _ => "Per user unless ALLUSERS=1 is passed",
                };
            }
        }

        /// <summary>The key properties that are present, then other public directory properties.</summary>
        public IReadOnlyList<MsiProperty> KeyProperties
        {
            get
            {
                var keys = KeyPropertyNames.Select(n => Properties.FirstOrDefault(p => p.Name == n)).OfType<MsiProperty>().ToList();
                keys.AddRange(Properties.Where(p => p.IsPublic
                                                    && (p.Name.EndsWith("DIR", StringComparison.Ordinal) || p.Name.EndsWith("FOLDER", StringComparison.Ordinal))
                                                    && !keys.Contains(p)));
                return keys;
            }
        }

        /// <summary>Properties whose name or value contains the text, ignoring case; blank returns all.</summary>
        public IReadOnlyList<MsiProperty> Search(string? text)
        {
            text = text?.Trim();
            if (string.IsNullOrEmpty(text)) return Properties;
            return Properties.Where(p => p.Name.Contains(text, StringComparison.OrdinalIgnoreCase)
                                         || p.Value.Contains(text, StringComparison.OrdinalIgnoreCase)).ToList();
        }
    }

    /// <summary>The platform half of the Template summary property ("x64;1033"). A blank platform is Intel.</summary>
    public static string PlatformOf(string template)
    {
        var platform = template.Split(';')[0].Trim();
        return platform.ToLowerInvariant() switch
        {
            "" or "intel" => "x86",
            "x64" or "amd64" => "x64",
            "intel64" => "Itanium",
            "arm64" => "ARM64",
            "arm" => "ARM",
            _ => platform,
        };
    }

    /// <summary>The language half of the Template summary property, each ID with its name where Windows knows one.</summary>
    public static string LanguagesOf(string template)
    {
        var at = template.IndexOf(';');
        if (at < 0) return "";
        return string.Join(", ", template[(at + 1)..].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(LanguageName));
    }

    private static string LanguageName(string id)
    {
        if (id == "0") return "Neutral";
        if (!int.TryParse(id, NumberStyles.None, CultureInfo.InvariantCulture, out var lcid)) return id;
        try
        {
            var name = CultureInfo.GetCultureInfo(lcid).EnglishName;
            return name.Length == 0 || name.StartsWith("Unknown", StringComparison.Ordinal) ? id : $"{name} ({id})";
        }
        catch (Exception ex) when (ex is CultureNotFoundException or ArgumentOutOfRangeException)
        {
            return id;
        }
    }

    /// <summary>
    /// Feature rows (<c>Feature</c>, <c>Title</c>, <c>Level</c>) as features. A level of 0 is
    /// disabled; a level up to <paramref name="installLevel"/> (the INSTALLLEVEL property, 1
    /// when unset) installs by default.
    /// </summary>
    public static IReadOnlyList<MsiFeature> ToFeatures(IEnumerable<string[]> rows, string installLevel)
    {
        if (!int.TryParse(installLevel, out var threshold)) threshold = 1;
        return rows.Where(r => r[0].Length > 0).Select(r =>
        {
            int.TryParse(r[2], out var level);
            var state = level == 0 ? "Disabled" : level <= threshold ? "Installed by default" : "Not installed by default";
            return new MsiFeature(r[0], r[1].Trim().Length == 0 ? state : $"{r[1].Trim()} · {state}");
        }).ToList();
    }

    // Control types that write what the user enters to their property.
    private static readonly Dictionary<string, string> InputControls = new(StringComparer.Ordinal)
    {
        ["CheckBox"] = "Check box",
        ["RadioButtonGroup"] = "Choice",
        ["ComboBox"] = "List",
        ["ListBox"] = "List",
        ["ListView"] = "List",
        ["Edit"] = "Text box",
        ["MaskedEdit"] = "Text box",
        ["PathEdit"] = "Folder",
        ["DirectoryCombo"] = "Folder",
    };
    private const int IndirectControl = 0x8;

    /// <summary>
    /// The public properties the dialogs set, in Control table order, one entry per property.
    /// <paramref name="controls"/> rows are <c>Type</c>, <c>Property</c>, <c>Text</c>,
    /// <c>Attributes</c>; <paramref name="checkBoxes"/> and <paramref name="choices"/> rows are
    /// <c>Property</c>, <c>Value</c>. An indirect control names the property that holds the
    /// real property's name.
    /// </summary>
    public static IReadOnlyList<MsiOption> ToOptions(IEnumerable<string[]> controls, IEnumerable<string[]> checkBoxes,
        IEnumerable<string[]> choices, IReadOnlyList<MsiProperty> properties)
    {
        string? Default(string name) => properties.FirstOrDefault(p => p.Name == name)?.Value;
        var checkedValues = checkBoxes.GroupBy(r => r[0]).ToDictionary(g => g.Key, g => g.First()[1]);
        var choiceValues = choices.GroupBy(r => r[0]).ToDictionary(g => g.Key, g => g.Select(r => r[1]).Distinct().ToList());

        var options = new List<MsiOption>();
        foreach (var row in controls)
        {
            if (!InputControls.TryGetValue(row[0], out var kind)) continue;
            var name = row[1];
            int.TryParse(row[3], out var attributes);
            if ((attributes & IndirectControl) != 0) name = Default(name) ?? "";
            if (!IsPublicName(name) || options.Any(o => o.Name == name)) continue;

            var isCheckBox = row[0] == "CheckBox";
            var label = isCheckBox ? ControlLabel(row[2]) : "";
            var values = isCheckBox ? [checkedValues.GetValueOrDefault(name, "1")] : choiceValues.GetValueOrDefault(name, []);
            var current = Default(name);

            var detail = new List<string> { label.Length == 0 ? kind : $"{kind} “{label}”" };
            if (isCheckBox) detail.Add($"Checked sets {values[0]}");
            else if (values.Count > 0) detail.Add($"Values: {string.Join(", ", values)}");
            detail.Add(string.IsNullOrEmpty(current) ? "Not set by default" : $"Default: {current}");

            var value = isCheckBox ? values[0] : !string.IsNullOrEmpty(current) ? current : values.FirstOrDefault() ?? "";
            options.Add(new MsiOption(name, string.Join(" · ", detail), value));
        }
        return options;
    }

    /// <summary>Control text without its leading {style} groups and accelerator marks.</summary>
    private static string ControlLabel(string text)
    {
        text = text.Trim();
        while (text.StartsWith('{'))
        {
            var close = text.IndexOf('}');
            if (close < 0) break;
            text = text[(close + 1)..].TrimStart();
        }
        return text.Replace("&&", "\u0000").Replace("&", "").Replace("\u0000", "&").Trim();
    }

    public static IReadOnlyList<string> FindTransforms(string msiPath)
    {
        var folder = Path.GetDirectoryName(Path.GetFullPath(msiPath));
        if (folder == null || !Directory.Exists(folder)) return [];

        try
        {
            var names = Directory.EnumerateFiles(folder, "*.mst").Select(f => Path.GetFileName(f)).ToList();
            names.Sort(StringComparer.OrdinalIgnoreCase);
            return names;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            // A folder that can't be listed must not hide the MSI's own properties.
            return [];
        }
    }

    /// <summary>
    /// <see cref="ExtractMsiInfo"/> on its own STA thread. The Installer COM object is
    /// apartment-threaded, so it is created, used and released on that one thread, and an MSI
    /// on a slow share doesn't hold up the UI.
    /// </summary>
    public static Task<MsiInfo> ExtractMsiInfoAsync(string msiPath)
    {
        var done = new TaskCompletionSource<MsiInfo>();
        var thread = new Thread(() =>
        {
            try { done.SetResult(ExtractMsiInfo(msiPath)); }
            catch (Exception ex) { done.SetException(ex); }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();
        return done.Task;
    }

    public static MsiInfo ExtractMsiInfo(string msiPath)
    {
        var info = new MsiInfo { Transforms = FindTransforms(msiPath) };
        try
        {
            Type? installerType = Type.GetTypeFromProgID("WindowsInstaller.Installer");
            if (installerType == null) return info;

            dynamic? installer = Activator.CreateInstance(installerType);
            if (installer == null) return info;

            // Typed as object so the helpers below bind statically.
            object database = installer.OpenDatabase(msiPath, 0);
            info.Properties = ReadRows(database, "SELECT `Property`, `Value` FROM `Property`", 2)
                .Select(r => new MsiProperty(r[0], r[1])).ToList();

            string Value(string name) => info.Properties.FirstOrDefault(p => p.Name == name)?.Value ?? "";
            info.ProductCode = Value("ProductCode");
            info.ProductName = Value("ProductName");
            info.ProductVersion = Value("ProductVersion");
            info.Manufacturer = Value("Manufacturer");
            info.UpgradeCode = Value("UpgradeCode");

            // Packaging needs only what is above. The rest is for the rail, and an MSI
            // without one of these tables or streams simply shows less.
            info.Summary = ReadSummary(database);
            info.Features = ToFeatures(
                TryReadRows(database, "SELECT `Feature`, `Title`, `Level` FROM `Feature`", 3), Value("INSTALLLEVEL"));
            info.Options = ToOptions(
                TryReadRows(database, "SELECT `Type`, `Property`, `Text`, `Attributes` FROM `Control`", 4),
                TryReadRows(database, "SELECT `Property`, `Value` FROM `CheckBox`", 2),
                [
                    .. TryReadRows(database, "SELECT `Property`, `Value` FROM `RadioButton`", 2),
                    .. TryReadRows(database, "SELECT `Property`, `Value` FROM `ComboBox`", 2),
                    .. TryReadRows(database, "SELECT `Property`, `Value` FROM `ListBox`", 2),
                    .. TryReadRows(database, "SELECT `Property`, `Value` FROM `ListView`", 2),
                ],
                info.Properties);

            Marshal.ReleaseComObject(database);
            Marshal.ReleaseComObject(installer);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"MSI extraction failed: {ex.Message}");
        }
        return info;
    }

    /// <summary>Every row of <paramref name="query"/>, its first <paramref name="columns"/> columns as text.</summary>
    private static List<string[]> ReadRows(object database, string query, int columns)
    {
        var rows = new List<string[]>();
        dynamic view = ((dynamic)database).OpenView(query);
        try
        {
            view.Execute();
            while (true)
            {
                dynamic? record = view.Fetch();
                if (record == null) break;
                var row = new string[columns];
                for (var i = 0; i < columns; i++) row[i] = (string)record.StringData[i + 1] ?? "";
                rows.Add(row);
                Marshal.ReleaseComObject(record);
            }
            view.Close();
        }
        finally
        {
            Marshal.ReleaseComObject(view);
        }
        return rows;
    }

    /// <summary><see cref="ReadRows"/>, or no rows when the MSI has no such table.</summary>
    private static List<string[]> TryReadRows(object database, string query, int columns)
    {
        try
        {
            return ReadRows(database, query, columns);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"MSI query skipped ({query}): {ex.Message}");
            return [];
        }
    }

    private static Dictionary<int, string> ReadSummary(object database)
    {
        var values = new Dictionary<int, string>();
        try
        {
            dynamic summary = ((dynamic)database).SummaryInformation[0];
            try
            {
                foreach (var id in SummaryIds)
                {
                    object? value = summary.Property[id];
                    var text = value switch
                    {
                        null => "",
                        DateTime date => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                        _ => Convert.ToString(value, CultureInfo.InvariantCulture) ?? "",
                    };
                    if (text.Length > 0) values[id] = text;
                }
            }
            finally
            {
                Marshal.ReleaseComObject(summary);
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"MSI summary information skipped: {ex.Message}");
        }
        return values;
    }
}
