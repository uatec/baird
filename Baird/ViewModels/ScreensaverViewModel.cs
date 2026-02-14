using System.Reactive;
using Baird.Models;
using Baird.Services;
using ReactiveUI;

namespace Baird.ViewModels;

public class ScreensaverViewModel : ReactiveObject
{
    private readonly ScreensaverService _service;

    private bool _isActive;
    public bool IsActive
    {
        get => _isActive;
        set => this.RaiseAndSetIfChanged(ref _isActive, value);
    }

    private ScreensaverAsset? _currentAsset;
    public ScreensaverAsset? CurrentAsset
    {
        get => _currentAsset;
        set => this.RaiseAndSetIfChanged(ref _currentAsset, value);
    }

    private string? _currentName;
    public string? CurrentName
    {
        get => _currentName;
        set => this.RaiseAndSetIfChanged(ref _currentName, value);
    }

    // We might want to pass formatted time or other metadata

    public ReactiveCommand<Unit, Unit> DeactivateCommand { get; }

    public ScreensaverViewModel(ScreensaverService service)
    {
        _service = service;
        DeactivateCommand = ReactiveCommand.Create(Deactivate);
    }

    public void Activate()
    {
        ScreensaverAsset? asset = _service.GetRandomScreensaver();
        if (asset != null)
        {
            CurrentAsset = asset;
            CurrentName = asset.CollectionName; // Or iterate name
            IsActive = true;
            // Log
            Console.WriteLine($"[ScreensaverViewModel] Activating screensaver: {CurrentName} - {CurrentAsset.VideoUrl}");
        }
        else
        {
            // Try again or rely on generic fallback?
            Console.WriteLine("[ScreensaverViewModel] No screensavers available.");
        }
    }

    public void Deactivate()
    {
        IsActive = false;
        CurrentAsset = null;
    }
}
