using Packman.Services;
using System.Collections.ObjectModel;
using System.Windows;

namespace Packman.ViewModels;

/// <summary>
/// The "Publishing…" overlay shared by the wizard's Review step and the standalone Upload
/// page: the step list, running/complete/succeeded flags, progress, cancel and done. The
/// owner supplies the work; this class owns the lifecycle around it.
/// </summary>
public sealed class PublishRunViewModel : ObservableObject
{
    // The order the upload service actually runs in.
    private static readonly string[] StepTitles =
    [
        "Signing with Authenticode",
        "Building .intunewin package",
        "Registering Win32 app",
        "Uploading to {0} tenant",
        "Publishing and assigning",
    ];
    private const int UploadStepIndex = 3;

    private CancellationTokenSource? _cts;
    private bool _succeeded;

    public ObservableCollection<PublishStepViewModel> Steps { get; }

    /// <summary>What the upload service reported, line by line, for the console.</summary>
    public ObservableCollection<PublishLogLine> Log { get; } = [];

    public string LogText => string.Join(Environment.NewLine, Log.Select(line => line.Display));

    /// <summary>Stops the run; the service removes the half-built app.</summary>
    public RelayCommand CancelCommand { get; }

    /// <summary>Dismisses the overlay once the run has finished.</summary>
    public RelayCommand DoneCommand { get; }

    /// <summary>Raised when the overlay is dismissed; true when the run had succeeded.</summary>
    public event Action<bool>? Dismissed;

    public PublishRunViewModel()
    {
        Steps = new ObservableCollection<PublishStepViewModel>(
            StepTitles.Select((title, i) => new PublishStepViewModel(i + 1, string.Format(title, "your"))));
        CancelCommand = new RelayCommand(Cancel, () => IsRunning);
        DoneCommand = new RelayCommand(() =>
        {
            var ok = _succeeded;
            IsPublishing = false;
            IsComplete = false;
            Dismissed?.Invoke(ok);
        }, () => IsComplete);
    }

    private bool _isPublishing;
    /// <summary>The overlay is showing, running or finished.</summary>
    public bool IsPublishing
    {
        get => _isPublishing;
        private set
        {
            if (!Set(ref _isPublishing, value)) return;
            OnPropertyChanged(nameof(IsNotPublishing));
            OnPropertyChanged(nameof(IsRunning));
            CancelCommand.RaiseCanExecuteChanged();
        }
    }
    public bool IsNotPublishing => !_isPublishing;

    private bool _isComplete;
    public bool IsComplete
    {
        get => _isComplete;
        private set
        {
            if (!Set(ref _isComplete, value)) return;
            OnPropertyChanged(nameof(IsRunning));
            OnPropertyChanged(nameof(IsSucceeded));
            OnPropertyChanged(nameof(IsFailed));
            CancelCommand.RaiseCanExecuteChanged();
            DoneCommand.RaiseCanExecuteChanged();
        }
    }

    /// <summary>Started but not finished. Drives the spinner.</summary>
    public bool IsRunning => _isPublishing && !_isComplete;
    public bool IsSucceeded => _isComplete && _succeeded && !HasWarnings;
    public bool IsFailed => _isComplete && !_succeeded;

    private bool _hasWarnings;
    public bool HasWarnings { get => _hasWarnings; private set => Set(ref _hasWarnings, value); }

    private string _title = "";
    public string Title { get => _title; private set => Set(ref _title, value); }

    private string _resultText = "";
    public string ResultText { get => _resultText; private set => Set(ref _resultText, value); }

    private string _statusText = "";
    public string StatusText { get => _statusText; private set => Set(ref _statusText, value); }

    private int _progressValue;
    public int ProgressValue { get => _progressValue; private set => Set(ref _progressValue, value); }

    public void Cancel()
    {
        StatusText = "Cancelling…";
        _cts?.Cancel();
    }

    /// <summary>
    /// Runs one publish on a worker thread and reflects it in the overlay. Returns the app
    /// id, or null when the run failed or was cancelled (the overlay shows why).
    /// </summary>
    public async Task<string?> RunAsync(
        string title,
        string tenantName,
        Func<IUploadProgress, CancellationToken, Task<string>> work,
        Func<string, string> successText,
        Func<string> cancelledText)
    {
        if (IsRunning) return null;

        Steps[UploadStepIndex] = new PublishStepViewModel(UploadStepIndex + 1, string.Format(StepTitles[UploadStepIndex], tenantName));
        foreach (var s in Steps) s.State = "pending";
        Steps[0].State = "working";

        Log.Clear();
        Title = title;
        ResultText = "";
        StatusText = "Starting upload…";
        ProgressValue = 0;
        _succeeded = false;
        HasWarnings = false;
        IsComplete = false;
        IsPublishing = true;

        _cts?.Dispose();
        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        var progress = new DispatchedProgress(this);

        try
        {
            var appId = await Task.Run(() => work(progress, token), token);
            ProgressValue = 100;
            foreach (var s in Steps) s.State = "done";
            ResultText = successText(appId);
            StatusText = $"Uploaded to Intune · App ID {appId}";
            _succeeded = true;
            return appId;
        }
        catch (IntuneFollowUpException ex)
        {
            ProgressValue = 100;
            foreach (var step in Steps) step.State = "done";
            Steps[^1].State = "error";
            HasWarnings = true;
            _succeeded = true; // The app exists: refresh callers when the result is dismissed.
            StatusText = "App retained in Intune · follow-up incomplete";
            ResultText = $"App ID {ex.AppId}{Environment.NewLine}{ex.Message}{Environment.NewLine}Review this app in Intune before publishing another copy.";
            return ex.AppId;
        }
        catch (OperationCanceledException)
        {
            MarkWorkingStepFailed();
            ResultText = cancelledText();
            StatusText = "Upload cancelled.";
            return null;
        }
        catch (Exception ex)
        {
            MarkWorkingStepFailed();
            ResultText = $"Upload failed: {ex.Message}";
            StatusText = ResultText;
            return null;
        }
        finally
        {
            IsComplete = true;
            _cts?.Dispose();
            _cts = null;
        }
    }

    private void MarkWorkingStepFailed()
    {
        var working = Steps.FirstOrDefault(s => s.State == "working");
        if (working != null) working.State = "error";
    }

    /// <summary>Maps the service's 0-100 progress onto the five steps.</summary>
    private void OnProgress(int pct, string message)
    {
        ProgressValue = pct;
        StatusText = message;

        int active =
            pct < 20 ? 0 :   // signing
            pct < 35 ? 1 :   // building
            pct < 45 ? 2 :   // registering
            pct < 95 ? 3 :   // uploading
                       4;    // publishing and assigning

        for (int i = 0; i < Steps.Count; i++)
        {
            if (i < active) { if (Steps[i].State != "done") Steps[i].State = "done"; }
            else if (i == active) { if (Steps[i].State == "pending") Steps[i].State = "working"; }
        }
    }

    private sealed class DispatchedProgress : IUploadProgress
    {
        private readonly PublishRunViewModel _vm;
        public DispatchedProgress(PublishRunViewModel vm) => _vm = vm;

        public void UpdateProgress(int percentage, string message) => OnUiThread(() => _vm.OnProgress(percentage, message));

        public void Log(UploadLogLevel level, string message)
        {
            var line = new PublishLogLine(DateTime.Now, level, message);
            OnUiThread(() => _vm.Log.Add(line));
        }

        private static void OnUiThread(Action action)
        {
            var dispatcher = Application.Current?.Dispatcher;
            if (dispatcher == null || dispatcher.CheckAccess())
                action();
            else
                dispatcher.InvokeAsync(action);
        }
    }
}
