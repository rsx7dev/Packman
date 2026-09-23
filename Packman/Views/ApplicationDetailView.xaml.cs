using Packman.Helpers;
using Packman.Models;
using Packman.Services;
using Packman.ViewModels;
using System;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;

namespace Packman.Views;

public partial class ApplicationDetailView : UserControl
{
    /// <summary>The app on show. Set by <see cref="Show"/>, or directly as DataContext by the UI previews.</summary>
    private ApplicationDetailViewModel? Vm => DataContext as ApplicationDetailViewModel;

    /// <summary>Raised by the breadcrumb; the host returns to the list.</summary>
    public event Action? BackRequested;

    /// <summary>Raised after a retire; the host returns to the list and refreshes.</summary>
    public event Action? Deleted;

    /// <summary>Raised after the package content was republished, so the list can refresh.</summary>
    public event Action? Updated;

    public ApplicationDetailView()
    {
        InitializeComponent();
        // The view outlives each app's view model, so it follows the sign-in and tells the current one.
        AppServices.Auth.StateChanged += () => Dispatcher.Invoke(() => Vm?.RaiseConnectionChanged());
    }

    /// <summary>Shows the app and starts the detail load.</summary>
    public void Show(IntuneApplication app)
    {
        var vm = new ApplicationDetailViewModel(app);
        DataContext = vm;
        ErrorReporter.FireAndForget(vm.LoadAsync);
    }

    private void Back_Click(object sender, RoutedEventArgs e) => BackRequested?.Invoke();

    private void ViewInIntune_Click(object sender, RoutedEventArgs e) => Vm?.OpenInIntune();

    /// <summary>
    /// Rebuilds the .intunewin from the source share and publishes it as new content for
    /// the same Intune app. Everything else about the app is left alone.
    /// </summary>
    private void Update_Click(object sender, RoutedEventArgs e)
    {
        if (Vm == null) return;

        if (!Vm.CanUpdatePackage)
        {
            MessageBox.Show(
                "No package folder was found on the share for this app, so there is nothing to rebuild.\n\nCheck the Intune Applications path on the Settings page.",
                "Package not found", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var result = MessageBox.Show(
            $"Rebuild the package for \"{Vm.Detail.DisplayName}\" and upload it as a new content version?\n\n" +
            $"Source: {Vm.SourcePath}\n\n" +
            "The .intunewin is regenerated from the source folder and becomes the content devices download. " +
            "Name, description, install commands, detection rules, requirements, return codes and assignments are not changed.",
            "Republish package content", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (result != MessageBoxResult.Yes) return;

        ErrorReporter.FireAndForget(async () =>
        {
            if (await Vm.UpdatePackageContentAsync()) Updated?.Invoke();
        });
    }

    private void CancelUpdate_Click(object sender, RoutedEventArgs e) => Vm?.CancelUpdate();

    private void ManageAssignments_Click(object sender, RoutedEventArgs e)
    {
        if (Vm != null) Vm.Tab = "deployment";
    }

    // ── Clipboard ──

    private static void CopyText(string? text)
    {
        if (string.IsNullOrEmpty(text)) return;
        try { Clipboard.SetText(text); }
        catch { /* clipboard can be locked by another process; not worth surfacing */ }
    }

    private void CopyPath_Click(object sender, RoutedEventArgs e) => CopyText(Vm?.SourcePath);
    private void CopyAppId_Click(object sender, RoutedEventArgs e) => CopyText(Vm?.Detail.Id);
    private void CopyInstall_Click(object sender, RoutedEventArgs e) => CopyText(Vm?.Detail.InstallCommand);
    private void CopyUninstall_Click(object sender, RoutedEventArgs e) => CopyText(Vm?.Detail.UninstallCommand);

    // ── Package source ──

    private void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        var path = Vm?.SourcePath;
        if (string.IsNullOrEmpty(path)) return;
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Could not open folder: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void EditScript_Click(object sender, RoutedEventArgs e)
    {
        var script = Vm?.SourceScriptPath;
        if (string.IsNullOrEmpty(script) || !File.Exists(script))
        {
            MessageBox.Show("No PSADT script found in the package's source folder.", "Not found",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        try
        {
            EditorLocator.Open(script);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Could not open script: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    // ── Detection rule editing ──

    private static T? RowContext<T>(object sender) where T : class =>
        (sender as FrameworkElement)?.DataContext as T;

    private void AddRule_Click(object sender, RoutedEventArgs e)
    {
        if (Vm == null) return;
        var type = ((NewRuleType.SelectedItem as ComboBoxItem)?.Content as string) switch
        {
            "MSI" => DetectionRuleType.MSI,
            "Registry" => DetectionRuleType.Registry,
            _ => DetectionRuleType.File,
        };
        Vm.AddDetectionRule(type);
    }

    private void EditRule_Click(object sender, RoutedEventArgs e) =>
        RowContext<DetectionRuleDisplay>(sender)?.BeginEdit();

    private void CancelRule_Click(object sender, RoutedEventArgs e)
    {
        var display = RowContext<DetectionRuleDisplay>(sender);
        if (display == null || Vm == null) return;
        if (display.IsNew)
            Vm.DiscardNewRule(display);
        else
            display.CancelEdit();
    }

    private void SaveRule_Click(object sender, RoutedEventArgs e)
    {
        var display = RowContext<DetectionRuleDisplay>(sender);
        if (display == null || Vm == null) return;
        display.ApplyEdit();
        ErrorReporter.FireAndForget(Vm.SaveDetectionRulesAsync);
    }

    private void DeleteRule_Click(object sender, RoutedEventArgs e)
    {
        var display = RowContext<DetectionRuleDisplay>(sender);
        if (display == null || Vm == null) return;

        var result = MessageBox.Show(
            "Delete this detection rule?\n\nIf no rule matches anymore, Intune considers the app not installed and required assignments will reinstall it.",
            "Delete detection rule", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (result != MessageBoxResult.Yes) return;

        ErrorReporter.FireAndForget(() => Vm.DeleteDetectionRuleAsync(display));
    }

    // ── Assignments ──

    private void AddAssignment_Click(object sender, RoutedEventArgs e)
    {
        if (Vm != null) ErrorReporter.FireAndForget(Vm.AddAssignmentAsync);
    }

    private void GroupResult_Click(object sender, RoutedEventArgs e)
    {
        var group = RowContext<EntraGroup>(sender);
        if (group != null) Vm?.SelectGroupResult(group);
    }

    private void RemoveAssignment_Click(object sender, RoutedEventArgs e)
    {
        var group = RowContext<AssignedGroup>(sender);
        if (group == null || Vm == null) return;

        var result = MessageBox.Show(
            $"Remove the {group.StatusLabel} assignment for \"{group.GroupName}\"?",
            "Remove assignment", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (result != MessageBoxResult.Yes) return;

        ErrorReporter.FireAndForget(() => Vm.RemoveAssignmentAsync(group));
    }

    // ── Group members slide-over ──

    private void GroupRow_Click(object sender, RoutedEventArgs e)
    {
        var group = RowContext<AssignedGroup>(sender);
        if (group != null && Vm != null) ErrorReporter.FireAndForget(() => Vm.OpenMembersAsync(group));
    }

    private void FlyoutClose_Click(object sender, RoutedEventArgs e) => Vm?.CloseFlyout();

    private void FlyoutBackdrop_MouseDown(object sender, System.Windows.Input.MouseButtonEventArgs e) => Vm?.CloseFlyout();

    private void MemberResult_Click(object sender, RoutedEventArgs e)
    {
        var member = RowContext<GroupMember>(sender);
        if (member != null && Vm != null) ErrorReporter.FireAndForget(() => Vm.AddMemberAsync(member));
    }

    private void RemoveMember_Click(object sender, RoutedEventArgs e)
    {
        var member = RowContext<GroupMember>(sender);
        if (member == null || Vm == null) return;

        var result = MessageBox.Show(
            $"Remove \"{member.DisplayName}\" from \"{Vm.FlyoutGroup?.GroupName}\"?\n\nThis affects every app assigned to the group.",
            "Remove member", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (result != MessageBoxResult.Yes) return;

        ErrorReporter.FireAndForget(() => Vm.RemoveMemberAsync(member));
    }

    private void Retire_Click(object sender, RoutedEventArgs e)
    {
        if (Vm == null) return;

        var result = MessageBox.Show(
            $"Delete \"{Vm.Detail.DisplayName}\" from Intune?\n\nThis permanently removes the Win32 app from the tenant. This cannot be undone.",
            "Delete from Intune", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (result != MessageBoxResult.Yes) return;

        ErrorReporter.FireAndForget(async () =>
        {
            if (await Vm.DeleteAsync()) Deleted?.Invoke();
        });
    }
}
