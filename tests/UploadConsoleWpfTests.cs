#:property TargetFramework=net10.0-windows
#:property UseWPF=true
#:property EnableWindowsTargeting=true
#:property PublishAot=false
#:property AssemblyName=UploadConsoleWpfTests
#:project ../Packman/Packman.csproj

using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Threading;
using Packman;
using Packman.Services;
using Packman.ViewModels;
using Packman.Views;
using Packman.Views.Controls;

// The publish console and the wizard's rail while a publish runs, offline: the real views and
// view models, with a fake upload in place of the service. Covers the console following the
// newest line, pausing when the owner scrolls up and resuming at the bottom, level colours, the
// Copy button, and the rail going Readiness -> progress -> result -> Readiness while the review
// stays on screen with its editing links disabled. Windows only (WPF).
//   dotnet run --file tests/UploadConsoleWpfTests.cs -c Release
// Exits non-zero when a check fails. Settings are never saved.
internal static class UploadConsoleWpfTests
{
    private const double RailWidth = 268;
    private const string Reported = "Registering application in Intune...";

    private static int _passed;
    private static readonly List<string> Failures = new();

    [STAThread]
    public static int Main()
    {
        var app = new App { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.InitializeComponent();

        Run("The console follows the newest line", ConsoleFollowsNewest);
        Run("Scrolling up pauses the console and scrolling back to the bottom resumes it", ConsolePausesAndResumes);
        Run("A new run follows again after the owner had scrolled up", NewRunFollowsAgain);
        Run("Each level has its own colour", LevelsHaveOwnColours);
        Run("Copy puts the timestamped console text on the clipboard", CopyPutsLogOnClipboard);
        Run("The rail goes from Readiness to progress and back while the review stays read-only", RailFollowsPublish);

        Console.WriteLine(Failures.Count == 0
            ? $"All {_passed} checks passed."
            : $"{Failures.Count} of {_passed + Failures.Count} checks failed:{Environment.NewLine}  " + string.Join(Environment.NewLine + "  ", Failures));
        return Failures.Count == 0 ? 0 : 1;
    }

    // ── Console ──

    private static void ConsoleFollowsNewest()
    {
        var (run, console, scroller) = NewConsole();
        AddLines(run, 60);
        Settle(console, RailWidth, 400);
        True(scroller.ScrollableHeight > 0, "the lines overflow the console");
        True(AtBottom(scroller), $"offset {scroller.VerticalOffset} of {scroller.ScrollableHeight}");
    }

    private static void ConsolePausesAndResumes()
    {
        var (run, console, scroller) = NewConsole();
        AddLines(run, 60);
        Settle(console, RailWidth, 400);

        scroller.ScrollToVerticalOffset(0);
        Settle(console, RailWidth, 400);
        AddLines(run, 10);
        Settle(console, RailWidth, 400);
        Equal(0.0, scroller.VerticalOffset);

        scroller.ScrollToEnd();
        Settle(console, RailWidth, 400);
        AddLines(run, 10);
        Settle(console, RailWidth, 400);
        True(AtBottom(scroller), $"offset {scroller.VerticalOffset} of {scroller.ScrollableHeight}");
    }

    private static void NewRunFollowsAgain()
    {
        var (run, console, scroller) = NewConsole();
        AddLines(run, 60);
        Settle(console, RailWidth, 400);
        scroller.ScrollToVerticalOffset(0);
        Settle(console, RailWidth, 400);

        run.Log.Clear();
        AddLines(run, 3);
        Settle(console, RailWidth, 400);
        AddLines(run, 60);
        Settle(console, RailWidth, 400);
        True(AtBottom(scroller), $"offset {scroller.VerticalOffset} of {scroller.ScrollableHeight}");
    }

    private static void LevelsHaveOwnColours()
    {
        var (run, console, _) = NewConsole();
        var levels = new[] { UploadLogLevel.Info, UploadLogLevel.Success, UploadLogLevel.Warning, UploadLogLevel.Error, UploadLogLevel.Section };
        foreach (var level in levels)
            run.Log.Add(new PublishLogLine(DateTime.Now, level, $"{level}-line"));
        Settle(console, RailWidth, 400);

        Color ColorOf(UploadLogLevel level) =>
            ((SolidColorBrush)Shown<TextBlock>(console).First(t => LineText(t).EndsWith($"{level}-line")).Foreground).Color;

        True(levels.Select(ColorOf).Distinct().Count() == levels.Length, "every level has a different colour");
        Equal(((SolidColorBrush)Application.Current.FindResource("WarnBrush")).Color, ColorOf(UploadLogLevel.Warning));
        Equal(((SolidColorBrush)Application.Current.FindResource("BadBrush")).Color, ColorOf(UploadLogLevel.Error));
    }

    private static void CopyPutsLogOnClipboard()
    {
        var run = new PublishRunViewModel();
        run.Log.Add(new PublishLogLine(new DateTime(2026, 1, 2, 3, 4, 5), UploadLogLevel.Info, "Authenticating with Microsoft Graph..."));
        run.Log.Add(new PublishLogLine(new DateTime(2026, 1, 2, 3, 4, 6), UploadLogLevel.Warning, "Block 2/9 attempt 1/5 failed: HTTP 503"));
        var console = new PublishConsole { DataContext = run };

        // The Copy confirmation is a tool tip, which needs the button in a real window.
        var window = new Window { Width = 320, Height = 420, Left = -32000, Top = 0, ShowActivated = false, Content = console };
        window.Show();
        try
        {
            Settle(console, RailWidth, 400);
            Clipboard.Clear();
            var copy = Shown<Button>(console).First(b => AutomationProperties.GetName(b) == "Copy console");
            copy.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));

            var expected = "03:04:05  Authenticating with Microsoft Graph..." + Environment.NewLine + "03:04:06  Block 2/9 attempt 1/5 failed: HTTP 503";
            Equal(expected, run.LogText);
            Equal(expected, Clipboard.GetText());
        }
        finally { window.Close(); }
    }

    // ── Rail and review ──

    private static void RailFollowsPublish()
    {
        var main = new MainViewModel { CurrentStepIndex = 2 };
        var publish = main.Upload.Publish;
        var rail = new WizardRail { DataContext = main };
        var review = new StepReview { DataContext = main };
        void Layout()
        {
            Settle(rail, RailWidth, 3000);
            Settle(review, 860, 3000);
        }

        bool Has<T>(DependencyObject root, Func<T, bool> match) where T : FrameworkElement => Shown<T>(root).Any(match);
        bool Text(DependencyObject root, string text) => Has<TextBlock>(root, t => t.Text == text);
        bool Named(DependencyObject root, string name) => Has<Button>(root, b => AutomationProperties.GetName(b) == name);
        bool EditLink(string text) => Shown<Button>(review).Where(b => Shown<TextBlock>(b).Any(t => t.Text == text)).Select(b => b.IsEnabled).DefaultIfEmpty(false).All(enabled => enabled);
        // Run remote test also needs a package, so it is only checked while publishing.
        bool Disabled(string text) => Shown<Button>(review).Where(b => Shown<TextBlock>(b).Any(t => t.Text == text)).Select(b => !b.IsEnabled).DefaultIfEmpty(false).All(off => off);

        Layout();
        True(Text(rail, "Publish readiness"), "readiness shows before publishing");
        True(!Text(rail, "Console"), "no console before publishing");
        True(Text(review, "Review & publish"), "review shows before publishing");
        True(EditLink("Edit package") && EditLink("Edit configuration"), "edit links enabled before publishing");

        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<string?>? running = null;
        Dispatcher.CurrentDispatcher.BeginInvoke(() => running = publish.RunAsync(
            "Publishing Contoso Reader…", "Contoso",
            async (progress, ct) =>
            {
                progress.UpdateProgress(45, Reported);
                progress.Log(UploadLogLevel.Info, Reported);
                await gate.Task;
                return "app-1";
            },
            id => $"Published successfully. App ID {id}",
            () => "Cancelled"));
        Pump(() => publish.IsRunning && publish.Log.Count == 1);
        Layout();

        True(!Text(rail, "Publish readiness"), "readiness hidden while publishing");
        True(Text(rail, "Publishing Contoso Reader…"), "publish title in the rail");
        True(Has<ProgressBar>(rail, _ => true), "overall progress bar");
        True(Text(rail, "Signing with Authenticode") && Text(rail, "Uploading to Contoso tenant"), "the step list");
        True(Named(rail, "Cancel publishing"), "Cancel while running");
        True(!Has<Button>(rail, b => b.Content as string == "Done"), "no Done while running");
        True(Text(rail, "Console") && Has<TextBlock>(rail, t => LineText(t).EndsWith(Reported) && LineText(t).Length > Reported.Length), "the console shows the reported line with its timestamp");
        True(Text(review, "Review & publish") && Text(review, "Command lines"), "the review stays on screen");
        True(Disabled("Edit package") && Disabled("Edit configuration") && Disabled("Run remote test"), "edit links disabled while publishing");

        gate.SetResult();
        Pump(() => publish.IsComplete);
        Layout();
        True(!Named(rail, "Cancel publishing"), "no Cancel once finished");
        True(Has<Button>(rail, b => b.Content as string == "Done"), "Done once finished");
        True(Text(rail, "Published successfully. App ID app-1"), "the result in the rail");
        True(Text(review, "Review & publish"), "the review stays after the run");
        Equal("app-1", running!.GetAwaiter().GetResult());

        publish.DoneCommand.Execute(null);
        Layout();
        True(Text(rail, "Publish readiness") && !Text(rail, "Console"), "readiness is back after Done");
        True(EditLink("Edit package") && EditLink("Edit configuration"), "edit links enabled after Done");
    }

    // ── Helpers ──

    private static (PublishRunViewModel Run, PublishConsole Console, ScrollViewer Scroller) NewConsole()
    {
        var run = new PublishRunViewModel();
        var console = new PublishConsole { DataContext = run };
        Settle(console, RailWidth, 400);
        return (run, console, (ScrollViewer)console.FindName("Scroller"));
    }

    private static void AddLines(PublishRunViewModel run, int count)
    {
        var start = run.Log.Count;
        for (var i = 0; i < count; i++)
            run.Log.Add(new PublishLogLine(DateTime.Now, UploadLogLevel.Info, $"Uploaded block {start + i + 1}/999"));
    }

    /// <summary>A console line is built from runs, which TextBlock.Text does not report.</summary>
    private static string LineText(TextBlock block) => string.Concat(block.Inlines.OfType<Run>().Select(run => run.Text));

    private static bool AtBottom(ScrollViewer scroller) => scroller.VerticalOffset >= scroller.ScrollableHeight - 1;

    /// <summary>Lays the element out and lets queued binding and scroll work run, until nothing moves.</summary>
    private static void Settle(FrameworkElement element, double width, double height)
    {
        for (var pass = 0; pass < 3; pass++)
        {
            Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.Background);
            element.Measure(new Size(width, height));
            element.Arrange(new Rect(0, 0, width, height));
            element.UpdateLayout();
        }
    }

    /// <summary>Runs the dispatcher, which the fake upload's continuations and progress need, until the condition holds.</summary>
    private static void Pump(Func<bool> until)
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(20) };
        timer.Tick += (_, _) =>
        {
            if (until() || DateTime.UtcNow > deadline) frame.Continue = false;
        };
        timer.Start();
        Dispatcher.PushFrame(frame);
        timer.Stop();
        True(until(), "timed out waiting for the publish state");
    }

    /// <summary>Descendants that took up space in the layout; collapsed ones measure to zero.</summary>
    private static IEnumerable<T> Shown<T>(DependencyObject root) where T : FrameworkElement
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is UIElement { Visibility: not Visibility.Visible }) continue;
            if (child is T match && match.ActualHeight > 0) yield return match;
            foreach (var nested in Shown<T>(child)) yield return nested;
        }
    }

    // ── Assertions ──

    private static void Run(string name, Action check)
    {
        try
        {
            check();
            _passed++;
            Console.WriteLine($"PASS  {name}");
        }
        catch (Exception ex)
        {
            Failures.Add($"{name}: {ex.Message}");
            Console.WriteLine($"FAIL  {name}: {ex}");
        }
    }

    private static void Equal<T>(T expected, T actual)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new Exception($"expected <{expected}>, got <{actual}>");
    }

    private static void True(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }
}
