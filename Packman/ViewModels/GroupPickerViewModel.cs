using Packman.Models;
using Packman.Services;
using System.Collections.ObjectModel;
using System.Linq;

namespace Packman.ViewModels;

/// <summary>
/// Group search-and-assign picker, shared by the wizard's Upload step and the standalone
/// Upload page. Each group carries its own intent, so the list can mix all three.
/// </summary>
public sealed class GroupPickerViewModel : ObservableObject
{
    private readonly IntuneAuthService _auth = AppServices.Auth;
    private readonly IntuneService _apps = AppServices.Apps;

    public GroupPickerViewModel()
    {
        SearchGroupsCommand = new AsyncRelayCommand(SearchGroupsAsync, () => !IsSearchingGroups);
        AddGroupCommand = new RelayCommand<EntraGroup>(AddGroup);
        RemoveGroupCommand = new RelayCommand<AssignedGroup>(g => { if (g != null) SelectedGroups.Remove(g); });
        SelectedGroups.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasGroups));
    }

    public ObservableCollection<EntraGroup> GroupResults { get; } = new();
    public ObservableCollection<AssignedGroup> SelectedGroups { get; } = new();

    public AsyncRelayCommand SearchGroupsCommand { get; }
    public RelayCommand<EntraGroup> AddGroupCommand { get; }
    public RelayCommand<AssignedGroup> RemoveGroupCommand { get; }

    private string _groupQuery = "";
    public string GroupQuery { get => _groupQuery; set => Set(ref _groupQuery, value); }

    private bool _isSearchingGroups;
    public bool IsSearchingGroups
    {
        get => _isSearchingGroups;
        private set { if (Set(ref _isSearchingGroups, value)) SearchGroupsCommand.RaiseCanExecuteChanged(); }
    }

    private string _groupSearchHint = "";
    public string GroupSearchHint { get => _groupSearchHint; private set => Set(ref _groupSearchHint, value); }

    private string _intent = "required"; // required | available | uninstall
    /// <summary>Intent applied to the next group added.</summary>
    public string Intent { get => _intent; set { if (Set(ref _intent, value)) OnPropertyChanged(nameof(IntentLabel)); } }
    public string IntentLabel => char.ToUpper(Intent[0]) + Intent[1..];

    public bool HasGroups => SelectedGroups.Count > 0;

    public async Task SearchGroupsAsync()
    {
        GroupResults.Clear();
        GroupSearchHint = "";
        if (string.IsNullOrWhiteSpace(GroupQuery))
            return;

        if (!_auth.IsSignedIn)
        {
            GroupSearchHint = "Sign in to Intune on the Settings page first.";
            return;
        }
        if (!_auth.HasScopes(IntuneAuthService.DirectoryScopes))
        {
            GroupSearchHint = IntuneAuthService.DirectoryAccessHint;
            return;
        }

        IsSearchingGroups = true;
        try
        {
            var found = await _apps.SearchGroupsAsync(GroupQuery);
            foreach (var g in found)
                GroupResults.Add(g);
            GroupSearchHint = found.Count == 0 ? "No groups match that name." : "";
        }
        catch (Exception ex)
        {
            GroupSearchHint = $"Search failed: {ex.Message}";
        }
        finally
        {
            IsSearchingGroups = false;
        }
    }

    private void AddGroup(EntraGroup? group)
    {
        if (group == null || string.IsNullOrEmpty(group.Id)) return;
        if (SelectedGroups.Any(g => g.GroupId == group.Id)) return;

        SelectedGroups.Add(new AssignedGroup
        {
            GroupId = group.Id,
            GroupName = group.DisplayName,
            AssignmentType = Intent,
        });

        GroupResults.Clear();
        GroupQuery = "";
    }

    /// <summary>
    /// Replaces the selection with the groups from Settings, resolving each name to an id.
    /// Unresolved names stay in the list without one so they are visible; the upload skips them.
    /// </summary>
    public async Task<bool> SeedFromSettingsAsync(AppSettings.GroupAssignmentConfig config)
    {
        var seq = ++_seedSeq;
        SelectedGroups.Clear();
        if (config.ExistingGroups.Count == 0) return true;

        if (!_auth.IsSignedIn)
        {
            GroupSearchHint = "Sign in to load the default groups from Settings.";
            return false;
        }
        if (!_auth.HasScopes(IntuneAuthService.DirectoryScopes))
        {
            GroupSearchHint = IntuneAuthService.DirectoryAccessHint;
            return false;
        }

        var unresolved = new List<string>();
        foreach (var existing in config.ExistingGroups)
        {
            var name = existing.GroupName.Trim();
            if (string.IsNullOrEmpty(name)) continue;
            if (SelectedGroups.Any(g => string.Equals(g.GroupName, name, StringComparison.OrdinalIgnoreCase))) continue;

            string id = "";
            try
            {
                var matches = await _apps.SearchGroupsAsync(name);
                id = matches.FirstOrDefault(m => string.Equals(m.DisplayName, name, StringComparison.OrdinalIgnoreCase))?.Id ?? "";
            }
            catch { /* leave unresolved */ }

            // A newer seed started while this one was awaiting; it owns the list now.
            if (seq != _seedSeq) return false;

            if (string.IsNullOrEmpty(id)) unresolved.Add(name);

            SelectedGroups.Add(new AssignedGroup
            {
                GroupId = id,
                GroupName = name,
                AssignmentType = IntentString(existing.Intent),
            });
        }

        GroupSearchHint = unresolved.Count == 0
            ? ""
            : $"Not found in Entra, will be skipped: {string.Join(", ", unresolved)}";
        return true;
    }

    /// <summary>Bumped per seed so an older, slower seed cannot append to a newer list.</summary>
    private int _seedSeq;

    /// <summary>The groups that can actually be assigned.</summary>
    public List<AssignedGroup> AssignableGroups =>
        SelectedGroups.Where(g => !string.IsNullOrWhiteSpace(g.GroupId)).ToList();

    private static string IntentString(AssignmentIntent intent) => intent switch
    {
        AssignmentIntent.Required => "required",
        AssignmentIntent.Uninstall => "uninstall",
        _ => "available",
    };
}
