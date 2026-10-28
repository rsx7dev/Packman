namespace Packman.ViewModels;

public sealed class StepViewModel : ObservableObject
{
    public int Index { get; }
    public string Title { get; }
    public string Number => (Index + 1).ToString();

    private bool _isCurrent;
    public bool IsCurrent
    {
        get => _isCurrent;
        set { if (Set(ref _isCurrent, value)) OnPropertyChanged(nameof(IsActive)); }
    }

    private bool _isDone;
    public bool IsDone
    {
        get => _isDone;
        set { if (Set(ref _isDone, value)) OnPropertyChanged(nameof(IsActive)); }
    }

    public bool IsActive => IsCurrent || IsDone;

    /// <summary>The last step has no connector to the next one.</summary>
    public bool IsLast { get; init; }

    private string? _lockReason;
    /// <summary>Why the stepper button is disabled, or null while it can be used.</summary>
    public string? LockReason { get => _lockReason; set => Set(ref _lockReason, value); }

    public StepViewModel(int index, string title)
    {
        Index = index;
        Title = title;
    }
}
