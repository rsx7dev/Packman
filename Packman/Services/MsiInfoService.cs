using Packman.Helpers;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;

namespace Packman.Services;

public class MsiInfoService
{
    /// <summary>One row of the MSI Property table.</summary>
    public record MsiProperty(string Name, string Value)
    {
        /// <summary>Public properties (no lowercase letter) can be set on the msiexec command line.</summary>
        public bool IsPublic => Name.Any(char.IsLetter) && !Name.Any(char.IsLower);

        /// <summary>The <c>NAME=value</c> text the install command takes.</summary>
        public string Argument => ArgumentLine.MsiProperty(Name, Value);
    }

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

    public static IReadOnlyList<string> FindTransforms(string msiPath)
    {
        var folder = Path.GetDirectoryName(Path.GetFullPath(msiPath));
        if (folder == null || !Directory.Exists(folder)) return [];

        var names = Directory.EnumerateFiles(folder, "*.mst").Select(f => Path.GetFileName(f)).ToList();
        names.Sort(StringComparer.OrdinalIgnoreCase);
        return names;
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

            dynamic database = installer.OpenDatabase(msiPath, 0);
            info.Properties = ReadProperties(database);

            string Value(string name) => info.Properties.FirstOrDefault(p => p.Name == name)?.Value ?? "";
            info.ProductCode = Value("ProductCode");
            info.ProductName = Value("ProductName");
            info.ProductVersion = Value("ProductVersion");
            info.Manufacturer = Value("Manufacturer");
            info.UpgradeCode = Value("UpgradeCode");

            Marshal.ReleaseComObject(database);
            Marshal.ReleaseComObject(installer);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"MSI extraction failed: {ex.Message}");
        }
        return info;
    }

    private static List<MsiProperty> ReadProperties(dynamic database)
    {
        var properties = new List<MsiProperty>();
        dynamic view = database.OpenView("SELECT `Property`, `Value` FROM `Property`");
        view.Execute();
        while (true)
        {
            dynamic? record = view.Fetch();
            if (record == null) break;
            properties.Add(new MsiProperty((string)record.StringData[1] ?? "", (string)record.StringData[2] ?? ""));
            Marshal.ReleaseComObject(record);
        }
        view.Close();
        Marshal.ReleaseComObject(view);
        return properties;
    }
}
