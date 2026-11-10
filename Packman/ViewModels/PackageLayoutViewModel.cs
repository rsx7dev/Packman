using Packman.Helpers;
using Packman.Models;
using System.Collections.ObjectModel;
using System.IO;

namespace Packman.ViewModels;

/// <summary>One line of the example package tree.</summary>
public sealed record TreeLine(int Depth, string Name, string Note, bool IsFolder)
{
    /// <summary>Left indent in the tree, in device-independent pixels.</summary>
    public double Indent => Depth * 16;

    // The name screen readers announce for a tree row.
    public override string ToString() => Name;
}

/// <summary>
/// The package folder choices: Icon and the extra folders. Shared by the setup's layout step and
/// the Settings tab through PackageLayoutEditor.
/// </summary>
public sealed class PackageLayoutViewModel : ObservableObject
{
    private bool _iconFolder = true;
    public bool IconFolder
    {
        get => _iconFolder;
        set { if (Set(ref _iconFolder, value)) OnPropertyChanged(nameof(TreeLines)); }
    }

    public ObservableCollection<string> ExtraFolders { get; } = new();

    private string _newFolderName = "";
    public string NewFolderName
    {
        get => _newFolderName;
        set { if (Set(ref _newFolderName, value) && FolderProblem.Length > 0) FolderProblem = ""; }
    }

    private string _folderProblem = "";
    /// <summary>Why the last Add was refused, or "".</summary>
    public string FolderProblem
    {
        get => _folderProblem;
        private set { if (Set(ref _folderProblem, value)) OnPropertyChanged(nameof(HasFolderProblem)); }
    }
    public bool HasFolderProblem => FolderProblem.Length > 0;

    public bool HasExtraFolders => ExtraFolders.Count > 0;

    public RelayCommand AddFolderCommand { get; }
    public RelayCommand<string> RemoveFolderCommand { get; }

    private static readonly string[] CommonFolders = ["Documentation", "Source", "Detection"];

    /// <summary>Common extra folders not added yet, offered as one-click additions.</summary>
    public IReadOnlyList<string> Suggestions =>
        CommonFolders.Where(name => PsadtLayout.ExtraFolderProblem(name, ExtraFolders) == null).ToList();
    public bool HasSuggestions => Suggestions.Count > 0;

    public RelayCommand<string> AddSuggestionCommand { get; }

    /// <summary>The example package, recomputed whenever the layout changes.</summary>
    public IReadOnlyList<TreeLine> TreeLines
    {
        get
        {
            // As Generate writes it: {app folder}\{version} under the Intune Applications Path.
            // A format that can't be used yet shows the default, so the tree never goes blank.
            var appFolder = PackagePaths.AppFolder(HasFolderNameProblem ? null : FolderNameTemplate, SampleVendor, SampleApp);
            var lines = appFolder.Split('\\').Select((level, depth) => new TreeLine(depth, level, "", true)).ToList();
            var d = lines.Count;
            lines.AddRange(
            [
                new(d, "1.0", "version", true),
                new(d + 1, PsadtLayout.ApplicationFolderName, "packed", true),
                new(d + 2, PsadtLayout.FilesFolderName, "installer", true),
                new(d + 2, PsadtLayout.ScriptName, "", false),
                new(d + 1, PsadtLayout.IntuneFolderName, "build output", true),
                new(d + 2, Path.ChangeExtension(PsadtLayout.SetupFileName, ".intunewin"), "", false),
                new(d + 2, PackageMarker.FileName, "link to app", false),
            ]);
            foreach (var folder in PsadtLayout.OptionalFolders(IconFolder, ExtraFolders))
                lines.Add(new TreeLine(d + 1, folder, folder == PsadtLayout.IconFolderName ? "app icon" : "empty", true));
            return lines;
        }
    }

    // ── Package folder name ───────────────────────────────────────────
    private const string SampleVendor = "Contoso";
    private const string SampleApp = "Acme Reader";

    private string _folderNameTemplate = PackagePaths.DefaultFolderTemplate;
    /// <summary>How a new package's folder is named; see <see cref="PackagePaths.AppFolder"/>.</summary>
    public string FolderNameTemplate
    {
        get => _folderNameTemplate;
        set => Set(ref _folderNameTemplate, value,
            [nameof(FolderNameProblem), nameof(HasFolderNameProblem), nameof(FolderNamePreview), nameof(TreeLines)]);
    }

    public string FolderNameProblem => PackagePaths.FolderTemplateProblem(_folderNameTemplate) ?? "";
    public bool HasFolderNameProblem => FolderNameProblem.Length > 0;

    /// <summary>The sample app's full relative folder, e.g. "Contoso_Acme_Reader\1.0".</summary>
    public string FolderNamePreview => HasFolderNameProblem ? ""
        : $@"{SampleVendor} {SampleApp} 1.0 is written to {PackagePaths.AppFolder(_folderNameTemplate, SampleVendor, SampleApp)}\1.0";

    /// <summary>Ready-made formats, one click each.</summary>
    public IReadOnlyList<string> FolderNamePresets { get; } =
        [PackagePaths.DefaultFolderTemplate, "%appName%", @"%vendor%\%appName%"];

    public RelayCommand<string> UseFolderNamePresetCommand { get; }

    public PackageLayoutViewModel()
    {
        AddFolderCommand = new RelayCommand(Add);
        RemoveFolderCommand = new RelayCommand<string>(Remove);
        AddSuggestionCommand = new RelayCommand<string>(name =>
        {
            if (PsadtLayout.ExtraFolderProblem(name, ExtraFolders) == null) ExtraFolders.Add(name);
        });
        UseFolderNamePresetCommand = new RelayCommand<string>(preset => FolderNameTemplate = preset);
        ExtraFolders.CollectionChanged += (_, _) =>
            RaiseAll(nameof(TreeLines), nameof(HasExtraFolders), nameof(Suggestions), nameof(HasSuggestions));
    }

    private void Add()
    {
        var name = NewFolderName.Trim();
        var problem = PsadtLayout.ExtraFolderProblem(name, ExtraFolders);
        if (problem != null)
        {
            FolderProblem = problem;
            return;
        }
        ExtraFolders.Add(name);
        NewFolderName = "";
        FolderProblem = "";
    }

    private void Remove(string name) => ExtraFolders.Remove(name);

    public void Load(AppSettings.PackageLayoutConfig config)
    {
        IconFolder = config.IconFolder;
        FolderNameTemplate = string.IsNullOrWhiteSpace(config.FolderNameTemplate)
            ? PackagePaths.DefaultFolderTemplate : config.FolderNameTemplate;
        ExtraFolders.Clear();
        foreach (var folder in config.ExtraFolders) ExtraFolders.Add(folder);
        NewFolderName = "";
        FolderProblem = "";
    }

    public AppSettings.PackageLayoutConfig ToConfig() => new()
    {
        IconFolder = IconFolder,
        ExtraFolders = ExtraFolders.ToList(),
        FolderNameTemplate = string.IsNullOrWhiteSpace(FolderNameTemplate)
            ? PackagePaths.DefaultFolderTemplate : FolderNameTemplate.Trim(),
    };
}
