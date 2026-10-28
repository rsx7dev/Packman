using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;

namespace Packman.Views.Controls;

/// <summary>
/// The strip a page footer shows while generate, publish or republish runs: job, phase,
/// progress, elapsed time and Cancel. Collapsed while <see cref="IsActive"/> is false.
/// </summary>
public partial class ActivityDock : UserControl
{
    public static readonly DependencyProperty IsActiveProperty =
        DependencyProperty.Register(nameof(IsActive), typeof(bool), typeof(ActivityDock),
            new PropertyMetadata(false, (d, _) => ((ActivityDock)d).OnIsActiveChanged()));

    public static readonly DependencyProperty JobNameProperty =
        DependencyProperty.Register(nameof(JobName), typeof(string), typeof(ActivityDock), new PropertyMetadata(""));

    public static readonly DependencyProperty PhaseProperty =
        DependencyProperty.Register(nameof(Phase), typeof(string), typeof(ActivityDock), new PropertyMetadata(""));

    /// <summary>0 to 100.</summary>
    public static readonly DependencyProperty ProgressProperty =
        DependencyProperty.Register(nameof(Progress), typeof(double), typeof(ActivityDock), new PropertyMetadata(0.0));

    /// <summary>For jobs that report no progress.</summary>
    public static readonly DependencyProperty IsIndeterminateProperty =
        DependencyProperty.Register(nameof(IsIndeterminate), typeof(bool), typeof(ActivityDock), new PropertyMetadata(false));

    public static readonly DependencyProperty CancelCommandProperty =
        DependencyProperty.Register(nameof(CancelCommand), typeof(ICommand), typeof(ActivityDock), new PropertyMetadata(null));

    private static readonly DependencyPropertyKey ElapsedKey =
        DependencyProperty.RegisterReadOnly(nameof(Elapsed), typeof(string), typeof(ActivityDock), new PropertyMetadata("0:00"));
    public static readonly DependencyProperty ElapsedProperty = ElapsedKey.DependencyProperty;

    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private DateTime _startedAt;

    public ActivityDock()
    {
        InitializeComponent();
        _timer.Tick += (_, _) => UpdateElapsed();
    }

    public bool IsActive { get => (bool)GetValue(IsActiveProperty); set => SetValue(IsActiveProperty, value); }
    public string JobName { get => (string)GetValue(JobNameProperty); set => SetValue(JobNameProperty, value); }
    public string Phase { get => (string)GetValue(PhaseProperty); set => SetValue(PhaseProperty, value); }
    public double Progress { get => (double)GetValue(ProgressProperty); set => SetValue(ProgressProperty, value); }
    public bool IsIndeterminate { get => (bool)GetValue(IsIndeterminateProperty); set => SetValue(IsIndeterminateProperty, value); }
    public ICommand? CancelCommand { get => (ICommand?)GetValue(CancelCommandProperty); set => SetValue(CancelCommandProperty, value); }

    /// <summary>Time since the job started, as m:ss.</summary>
    public string Elapsed => (string)GetValue(ElapsedProperty);

    private void OnIsActiveChanged()
    {
        if (!IsActive)
        {
            _timer.Stop();
            return;
        }
        _startedAt = DateTime.UtcNow;
        UpdateElapsed();
        _timer.Start();
    }

    private void UpdateElapsed()
    {
        var elapsed = DateTime.UtcNow - _startedAt;
        SetValue(ElapsedKey, $"{(int)elapsed.TotalMinutes}:{elapsed.Seconds:00}");
    }
}
