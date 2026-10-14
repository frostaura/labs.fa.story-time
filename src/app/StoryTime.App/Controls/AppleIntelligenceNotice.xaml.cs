using StoryTime.Core.Services;

namespace StoryTime.App.Controls;

/// <summary>Binds to the app's <see cref="IStoryEngineStatus"/> and re-reads it whenever the
/// control loads and whenever the app returns to the foreground (the parent may have just
/// turned Apple Intelligence on in Settings).</summary>
public partial class AppleIntelligenceNotice : ContentView
{
    private IStoryEngineStatus? _status;
    private Window? _window;

    public AppleIntelligenceNotice()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private void OnLoaded(object? sender, EventArgs e)
    {
        _status ??= Handler?.MauiContext?.Services.GetService<IStoryEngineStatus>();
        if (_status is null) return;
        BindingContext = _status;
        if (_status is System.ComponentModel.INotifyPropertyChanged observable)
        {
            observable.PropertyChanged -= OnStatusChanged;
            observable.PropertyChanged += OnStatusChanged;
        }
        _window = Window;
        if (_window is not null)
        {
            _window.Resumed -= OnResumed;
            _window.Resumed += OnResumed;
        }
        _status.Refresh();
        IsVisible = _status.ShowGuidance;
    }

    private void OnUnloaded(object? sender, EventArgs e)
    {
        if (_window is not null) _window.Resumed -= OnResumed;
        if (_status is System.ComponentModel.INotifyPropertyChanged observable)
            observable.PropertyChanged -= OnStatusChanged;
    }

    private void OnResumed(object? sender, EventArgs e) => _status?.Refresh();

    private void OnStatusChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e) =>
        Dispatcher.Dispatch(() => IsVisible = _status?.ShowGuidance ?? false);
}
