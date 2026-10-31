using Packman.Helpers;
using Packman.Models;
using System.Collections.ObjectModel;

namespace Packman.ViewModels;

/// <summary>One line of the example package tree.</summary>
public sealed record TreeLine(int Depth, string Name, string Note, bool IsFolder)
{
    /// <summary>Left indent in the tree, in device-independent pixels.</summary>
    public double Indent => Depth * 20;

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

    /// <summary>The example package, recomputed whenever the layout changes.</summary>
    public IReadOnlyList<TreeLine> TreeLines
    {
        get
        {
            var lines = new List<TreeLine>
            {
                new(0, "Acme Reader 1.0", "", true),
                new(1, PsadtLayout.ApplicationFolderName, "PSADT files", true),
                new(2, PsadtLayout.FilesFolderName, "installer", true),
                new(2, PsadtLayout.ScriptName, "", false),
                new(1, PsadtLayout.IntuneFolderName, "build output", true),
                new(2, "Acme Reader.intunewin", "", false),
                new(2, PackageMarker.FileName, "link to app", false),
            };
            foreach (var folder in PsadtLayout.OptionalFolders(IconFolder, ExtraFolders))
                lines.Add(new TreeLine(1, folder, folder == PsadtLayout.IconFolderName ? "app icon" : "empty", true));
            return lines;
        }
    }

    public PackageLayoutViewModel()
    {
        AddFolderCommand = new RelayCommand(Add);
        RemoveFolderCommand = new RelayCommand<string>(Remove);
        ExtraFolders.CollectionChanged += (_, _) => RaiseAll(nameof(TreeLines), nameof(HasExtraFolders));
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
        ExtraFolders.Clear();
        foreach (var folder in config.ExtraFolders) ExtraFolders.Add(folder);
        NewFolderName = "";
        FolderProblem = "";
    }

    public AppSettings.PackageLayoutConfig ToConfig() => new()
    {
        IconFolder = IconFolder,
        ExtraFolders = ExtraFolders.ToList(),
    };
}
