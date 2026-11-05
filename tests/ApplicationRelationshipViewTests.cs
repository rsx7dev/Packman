#:property TargetFramework=net10.0-windows
#:property UseWPF=true
#:property EnableWindowsTargeting=true
#:property PublishAot=false
#:property AssemblyName=Packman.Tests
#:project ../Packman/Packman.csproj

using System.IO;
using System.Net;
using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using Packman;
using Packman.Models;
using Packman.Services;
using Packman.ViewModels;
using Packman.Views;

// The real detail view, loaded through fake Graph responses. Windows only:
// dotnet run --file tests/ApplicationRelationshipViewTests.cs -c Release
internal static class ApplicationRelationshipViewTests
{
    [STAThread]
    public static void Main()
    {
        var app = new App { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.InitializeComponent();
        var folder = Directory.CreateTempSubdirectory("packman-relationships-").FullName;
        var oldRoot = AppServices.Settings.Settings.NetworkPaths.IntuneApplications;
        var oldHttp = GraphClient.Http;
        using var handler = new RelationshipHandler();
        using var http = new HttpClient(handler);
        try
        {
            GraphClient.Http = http;
            AppServices.Settings.Settings.NetworkPaths.IntuneApplications = folder;
            AppServices.Auth.UseTestSession("relationship-tests", "test-token");
            var vm = new ApplicationDetailViewModel(new IntuneApplication { Id = "app-1" });
            Check(!vm.HasRelationshipData, "Unloaded relationships must not appear empty.");
            vm.LoadAsync().GetAwaiter().GetResult();
            Check(vm.HasRelationshipData, "Graph relationships did not reach the view model.");

            foreach (var theme in new[] { "Dark", "Light" })
            {
                app.Resources.MergedDictionaries.Add(new ResourceDictionary
                {
                    Source = new Uri($"pack://application:,,,/Packman;component/Themes/{theme}Theme.xaml"),
                });
                var view = new ApplicationDetailView { DataContext = vm };
                Layout(view);
                var section = (Border)view.FindName("RelationshipsSection");
                var text = Shown<TextBlock>(section).Select(t => t.Text).ToList();
                foreach (var expected in new[] { "Dependencies", "Runtime", "8.0", "Supersedes", "Older app", "1.0", "Superseded by", "Newer app", "3.0" })
                    Check(text.Contains(expected), $"{theme}: missing relationship text {expected}.");
                Check(!Shown<ButtonBase>(section).Any() && !Shown<TextBoxBase>(section).Any() && !Shown<Selector>(section).Any(),
                    $"{theme}: relationship sections must have no editing controls.");
                Check(!text.Contains("None"), $"{theme}: populated lists showed the empty state.");
            }

            handler.Body = "{\"value\":[]}";
            vm.LoadAsync().GetAwaiter().GetResult();
            var emptyView = new ApplicationDetailView { DataContext = vm };
            Layout(emptyView);
            var emptySection = (Border)emptyView.FindName("RelationshipsSection");
            Check(Shown<TextBlock>(emptySection).Count(t => t.Text == "None") == 3, "Empty lists must show three empty states.");

            handler.Status = HttpStatusCode.Forbidden;
            vm.LoadAsync().GetAwaiter().GetResult();
            Layout(emptyView);
            Check(!vm.HasRelationshipData, "Failed reads must hide the lists.");
            var errorText = Shown<TextBlock>(emptySection).Select(t => t.Text).ToList();
            Check(errorText.Any(t => t.Contains("Could not load relationships")), "The view must show the read error.");
            Check(!errorText.Contains("None"), "A failed read must not look like no relationships.");
            Console.WriteLine("PASS: Graph dependency and supersedence rows render in both themes; lists are read-only; empty and failed reloads update the view.");
        }
        finally
        {
            GraphClient.Http = oldHttp;
            AppServices.Settings.Settings.NetworkPaths.IntuneApplications = oldRoot;
            AppServices.Auth.SignOutAsync().GetAwaiter().GetResult();
            Directory.Delete(folder, recursive: true);
            app.Shutdown();
        }
    }

    private static void Layout(ApplicationDetailView view)
    {
        view.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        view.Measure(new Size(1320, 2400));
        view.Arrange(new Rect(0, 0, 1320, 2400));
        view.UpdateLayout();
    }

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

    private static void Check(bool passed, string message)
    {
        if (!passed) throw new InvalidOperationException(message);
    }

    private sealed class RelationshipHandler : HttpMessageHandler
    {
        public HttpStatusCode Status { get; set; } = HttpStatusCode.OK;
        public string Body { get; set; } = """
            {"value":[
              {"@odata.type":"#microsoft.graph.mobileAppDependency","targetType":"child","targetId":"runtime","targetDisplayName":"Runtime","targetDisplayVersion":"8.0"},
              {"@odata.type":"#microsoft.graph.mobileAppSupersedence","targetType":"child","targetId":"old","targetDisplayName":"Older app","targetDisplayVersion":"1.0"},
              {"@odata.type":"#microsoft.graph.mobileAppSupersedence","targetType":"parent","targetId":"new","targetDisplayName":"Newer app","targetDisplayVersion":"3.0"}
            ]}
            """;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var path = request.RequestUri!.AbsolutePath;
            var (status, body) = path switch
            {
                "/beta/deviceAppManagement/mobileApps/app-1" => (HttpStatusCode.OK, """{"id":"app-1","displayName":"Current app","publishingState":"published"}"""),
                "/beta/deviceAppManagement/mobileApps/app-1/relationships" => (Status, Body),
                "/beta/deviceAppManagement/mobileApps/app-1/assignments" => (HttpStatusCode.OK, "{\"value\":[]}"),
                "/beta/deviceManagement/reports/getAppStatusOverviewReport" => (HttpStatusCode.OK, "{}"),
                _ => throw new InvalidOperationException($"Unexpected Graph request: {path}"),
            };
            return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body) });
        }
    }
}
