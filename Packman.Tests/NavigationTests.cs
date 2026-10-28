using System.Xml.Linq;
using Packman.Models;
using Packman.Services;
using Packman.ViewModels;
using Xunit;

namespace Packman.Tests;

/// <summary>The left nav / bottom menu bar toggle: view model, persistence and the markup that wires it up.</summary>
public class NavigationTests
{
    private static readonly string RepoRoot = FindRepoRoot();

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Packman.sln"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Packman.sln not found above the test output.");
    }

    private static string ReadSource(params string[] relative) =>
        File.ReadAllText(Path.Combine(new[] { RepoRoot }.Concat(relative).ToArray()));

    private static XElement LoadMainWindow() =>
        XDocument.Parse(ReadSource("Packman", "MainWindow.xaml")).Root!;

    private static readonly XNamespace Xaml = "http://schemas.microsoft.com/winfx/2006/xaml";

    private static string? Named(XElement element, string name) => (string?)element.Attribute(Xaml + name);

    private static IEnumerable<XElement> NavButtons(XElement window) =>
        window.Descendants().Where(e => e.Name.LocalName == "RadioButton" && Named(e, "Name")?.EndsWith("NavBtn") == true);

    private static IEnumerable<XElement> MenuBarItems(XElement window) =>
        window.Descendants().Where(e => e.Name.LocalName == "RadioButton" &&
                                        (string?)e.Attribute("Style") == "{StaticResource MenuBarItem}");

    // WPF view models create dispatcher timers, so build them on an STA thread like the app does.
    private static void RunSta(Action body)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { body(); }
            catch (Exception ex) { failure = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure != null) throw new Xunit.Sdk.XunitException(failure.ToString());
    }

    [Fact]
    public void Nav_is_visible_by_default()
    {
        Assert.False(new AppSettings().NavHidden);
    }

    [Fact]
    public void Toggle_command_swaps_nav_and_menu_bar_and_the_choice_survives_a_restart()
    {
        RunSta(() =>
        {
            var original = AppServices.Settings.Settings.NavHidden;
            try
            {
                var vm = new MainViewModel();
                vm.IsNavHidden = false;

                vm.ToggleNavCommand.Execute(null);
                Assert.True(vm.IsNavHidden);
                // A fresh SettingsService reads the file from disk, as a restart would.
                Assert.True(new SettingsService().Settings.NavHidden);
                Assert.True(new MainViewModel().IsNavHidden);

                vm.ToggleNavCommand.Execute(null);
                Assert.False(vm.IsNavHidden);
                Assert.False(new SettingsService().Settings.NavHidden);
            }
            finally
            {
                AppServices.Settings.Settings.NavHidden = original;
                AppServices.Settings.Save();
            }
        });
    }

    [Fact]
    public void Nav_and_menu_bar_visibility_follow_the_same_flag_in_opposite_directions()
    {
        var window = LoadMainWindow();

        var nav = NavButtons(window).First().Parent!.Parent!;
        Assert.Equal("{Binding IsNavHidden, Converter={StaticResource InverseBoolToVis}}", (string?)nav.Attribute("Visibility"));

        var menuBar = MenuBarItems(window).First().Parent!.Parent!;
        Assert.Equal("{Binding IsNavHidden, Converter={StaticResource BoolToVis}}", (string?)menuBar.Attribute("Visibility"));

        // Neither surface sits inside the other, so hiding one never hides or shows the other.
        Assert.DoesNotContain(menuBar, nav.Descendants());
        Assert.DoesNotContain(nav, menuBar.Descendants());
    }

    [Fact]
    public void Every_menu_bar_item_is_bound_to_its_nav_button()
    {
        var window = LoadMainWindow();

        var navNames = NavButtons(window).Select(b => Named(b, "Name")).Order().ToList();
        var targets = MenuBarItems(window)
            .Select(b => (string?)b.Attribute("IsChecked") ?? "")
            .Select(binding => binding.Split("ElementName=")[1].Split(',', '}')[0])
            .Order().ToList();

        Assert.Equal(6, navNames.Count);
        Assert.Equal(navNames, targets);
    }

    [Fact]
    public void Ctrl_B_and_the_title_bar_button_both_run_the_toggle_command()
    {
        var window = LoadMainWindow();

        var binding = window.Descendants().Single(e => e.Name.LocalName == "KeyBinding");
        Assert.Equal("B", (string?)binding.Attribute("Key"));
        Assert.Equal("Control", (string?)binding.Attribute("Modifiers"));
        Assert.Equal("{Binding ToggleNavCommand}", (string?)binding.Attribute("Command"));

        var button = window.Descendants().Single(e => e.Name.LocalName == "Button" &&
                                                      ((string?)e.Attribute("ToolTip"))?.Contains("Ctrl+B") == true);
        Assert.Equal("{Binding ToggleNavCommand}", (string?)button.Attribute("Command"));
    }

    [Fact]
    public void Ctrl_B_inside_the_script_editor_is_forwarded_to_the_toggle_command()
    {
        // WPF never sees keys while the Monaco WebView has focus, so the editor posts a message like it does for Ctrl+S.
        var editor = ReadSource("Packman", "MonacoEditor", "index.html");
        Assert.Contains("monaco.KeyMod.CtrlCmd | keyB", editor);
        Assert.Contains("post({ type: 'toggleNav' })", editor);

        var view = ReadSource("Packman", "Views", "StepEdit.xaml.cs");
        Assert.Contains("case \"toggleNav\":", view);
        Assert.Contains("ToggleNavCommand.Execute", view);
    }
}
