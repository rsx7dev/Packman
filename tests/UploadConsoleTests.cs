#:property TargetFramework=net10.0
#:property PublishAot=false

using System.IO.Pipes;
using Packman.Helpers;
using Packman.Services;

// What the publish console receives from UploadLogger: every level reaches it, metadata does
// not, and no SAS query string, token, client secret or certificate thumbprint survives. Also
// that a child's output is forwarded line by line while it runs, and kept when the run is cancelled.
// The console's scrolling, Copy and the rail's states are in UploadConsoleWpfTests.cs.
// Uses the production sources; the log file goes to the local app data folder as in production.
//   dotnet run --file tests/UploadConsoleTests.cs -c Release
// Exits non-zero when a check fails.
internal static class UploadConsoleTests
{
    private const string Sas = "https://stg.blob.core.windows.net/c/f?sv=2023-01-01&se=2030-01-01T00%3A00%3A00Z&sp=rw&sig=AbC%2Fdef%3D";

    private static int _passed;
    private static readonly List<string> Failures = new();

    private static readonly (string Name, Action Check)[] Checks =
    [
        ("Each level reaches the console with its text", LevelsReachConsole),
        ("Progress lines arrive as info and metadata stays out", ProgressInfoMetadataOut),
        ("A SAS URL keeps its path and loses the query string", SasQueryRemoved),
        ("Bearer tokens, JWTs and secret pairs are masked", TokensMasked),
        ("A certificate thumbprint is masked", ThumbprintMasked),
        ("An error with an exception shows the message and no stack trace", ErrorShowsMessageOnly),
        ("Ordinary text is left alone", PlainTextUntouched),
        ("Process output reaches the callback before the stream ends", OutputArrivesWhileStreamOpen),
        ("Output already forwarded survives a cancel", OutputSurvivesCancel),
    ];

    private sealed class Capture : IUploadProgress
    {
        public List<(UploadLogLevel Level, string Text)> Lines { get; } = new();
        public void UpdateProgress(int percentage, string message) { }
        public void Log(UploadLogLevel level, string message) => Lines.Add((level, message));
    }

    public static int Main()
    {
        foreach (var (name, check) in Checks)
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
                Console.WriteLine($"FAIL  {name}: {ex.Message}");
            }
        }

        Console.WriteLine(Failures.Count == 0
            ? $"All {_passed} checks passed."
            : $"{Failures.Count} of {_passed + Failures.Count} checks failed:{Environment.NewLine}  " + string.Join(Environment.NewLine + "  ", Failures));
        return Failures.Count == 0 ? 0 : 1;
    }

    private static List<(UploadLogLevel Level, string Text)> Run(Action<UploadLogger> write)
    {
        var capture = new Capture();
        using (var log = new UploadLogger("Packman-QA-console-test", console: capture))
            write(log);
        return capture.Lines;
    }

    private static void Expect(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }

    private static void LevelsReachConsole()
    {
        var lines = Run(log =>
        {
            log.Info("a");
            log.Success("b");
            log.Warning("c");
            log.Error("d");
            log.Section("E");
        });
        var expected = new[] { (UploadLogLevel.Info, "a"), (UploadLogLevel.Success, "b"), (UploadLogLevel.Warning, "c"), (UploadLogLevel.Error, "d"), (UploadLogLevel.Section, "E") };
        Expect(lines.SequenceEqual(expected), $"got: {string.Join(" | ", lines)}");
    }

    private static void ProgressInfoMetadataOut()
    {
        var lines = Run(log =>
        {
            log.Progress(35, "Registering application in Intune...");
            log.LogMetadata("Install Command", "setup.exe /password=hunter2");
        });
        Expect(lines.Count == 1 && lines[0] is (UploadLogLevel.Info, "Registering application in Intune..."), $"got: {string.Join(" | ", lines)}");
    }

    private static void SasQueryRemoved()
    {
        var lines = Run(log =>
        {
            log.Success($"Azure Storage upload URL obtained: {Sas}");
            log.Warning($"Retrying {Sas} now");
        });
        Expect(lines[0].Text == "Azure Storage upload URL obtained: https://stg.blob.core.windows.net/c/f", lines[0].Text);
        Expect(lines[1].Text == "Retrying https://stg.blob.core.windows.net/c/f now", lines[1].Text);
    }

    private static void TokensMasked()
    {
        var jwt = "eyJhbGciOiJSUzI1NiJ9.eyJzdWIiOiIxMjM0In0.c2lnbmF0dXJl";
        var lines = Run(log =>
        {
            log.Error($"Graph said: Authorization: Bearer abc.DEF-123_x~+/= denied");
            log.Error($"token {jwt} rejected");
            log.Error("client_secret=s3cr3t&grant_type=client_credentials failed");
        });
        Expect(lines[0].Text == "Graph said: Authorization: *** denied", lines[0].Text);
        Expect(lines[1].Text == "token *** rejected", lines[1].Text);
        Expect(lines[2].Text == "client_secret=***&grant_type=client_credentials failed", lines[2].Text);
    }

    private static void ThumbprintMasked()
    {
        var lines = Run(log => log.Error("No certificate with thumbprint 0123456789ABCDEF0123456789ABCDEF01234567 found"));
        Expect(lines[0].Text == "No certificate with thumbprint *** found", lines[0].Text);
    }

    private static void ErrorShowsMessageOnly()
    {
        var lines = Run(log =>
        {
            try { throw new InvalidOperationException($"PUT {Sas} failed"); }
            catch (Exception ex) { log.Error("Upload process failed", ex); }
        });
        Expect(lines.Count == 1, $"expected one line, got {lines.Count}");
        Expect(lines[0].Text == "Upload process failed: PUT https://stg.blob.core.windows.net/c/f failed", lines[0].Text);
    }

    private static void PlainTextUntouched()
    {
        const string text = "Assigned 'Packman-QA group' (Required) - block 3/12 (18 MB of 72 MB) at https://intune.microsoft.com/apps";
        var lines = Run(log => log.Info(text));
        Expect(lines[0].Text == text, lines[0].Text);
    }

    private static (AnonymousPipeServerStream Child, StreamWriter Writer, StreamReader Reader) Pipe()
    {
        var server = new AnonymousPipeServerStream(PipeDirection.Out);
        var client = new AnonymousPipeClientStream(PipeDirection.In, server.ClientSafePipeHandle);
        return (server, new StreamWriter(server) { AutoFlush = true }, new StreamReader(client));
    }

    private static void OutputArrivesWhileStreamOpen()
    {
        var (server, writer, reader) = Pipe();
        using (server)
        {
            var first = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            var seen = new List<string>();
            var read = ProcessOutput.ReadLinesAsync(reader, line => { seen.Add(line); first.TrySetResult(line); }, CancellationToken.None);

            writer.WriteLine("  Packaging files  ");
            Expect(first.Task.Wait(TimeSpan.FromSeconds(5)), "no line arrived while the stream was still open");
            Expect(!read.IsCompleted, "the read finished before the stream closed");
            Expect(first.Task.Result == "Packaging files", $"line not trimmed: '{first.Task.Result}'");

            writer.WriteLine("");
            writer.WriteLine("Done");
            writer.Dispose();
            var all = read.GetAwaiter().GetResult();
            Expect(seen.SequenceEqual(new[] { "Packaging files", "Done" }), $"blank lines must be skipped, got: {string.Join(" | ", seen)}");
            Expect(all.Contains("Done") && all.Contains("Packaging files"), "the full text is returned for the error message");
        }
    }

    private static void OutputSurvivesCancel()
    {
        var (server, writer, reader) = Pipe();
        using (server)
        using (var cts = new CancellationTokenSource())
        {
            var first = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            var seen = new List<string>();
            var read = ProcessOutput.ReadLinesAsync(reader, line => { seen.Add(line); first.TrySetResult(line); }, cts.Token);

            writer.WriteLine("Building package");
            Expect(first.Task.Wait(TimeSpan.FromSeconds(5)), "no line arrived");
            cts.Cancel();
            try { read.GetAwaiter().GetResult(); throw new Exception("the read did not observe the cancel"); }
            catch (OperationCanceledException) { }
            Expect(seen.SequenceEqual(new[] { "Building package" }), $"got: {string.Join(" | ", seen)}");
        }
    }
}
