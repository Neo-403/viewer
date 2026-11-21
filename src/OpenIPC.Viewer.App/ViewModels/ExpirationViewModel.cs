using System;
using System.Windows.Input;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using CommunityToolkit.Mvvm.Input;
using OpenIPC.Viewer.App.Services;
using OpenIPC.Viewer.Core.Expiration;

namespace OpenIPC.Viewer.App.ViewModels;

/// <summary>
/// The only view model a blocked time-limited build ever creates: no IoC
/// services, no database, no cameras — just the failure reason and an exit button.
/// </summary>
public sealed class ExpirationViewModel
{
    public ExpirationViewModel(ExpirationBlockReason reason)
    {
        var key = reason == ExpirationBlockReason.TimeUnavailable
            ? "Expiration.Offline"
            : "Expiration.Incompatible";
        Title = Localizer.Instance[$"{key}.Title"];
        Message = Localizer.Instance[$"{key}.Message"];
        ExitText = Localizer.Instance["Expiration.Exit"];
        ExitCommand = new RelayCommand(Exit);
    }

    public string Title { get; }
    public string Message { get; }
    public string ExitText { get; }
    public string? ExpiresAtText => null;
    public bool HasExpiresAtText => false;
    public ICommand ExitCommand { get; }

    private static void Exit()
    {
        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            desktop.Shutdown(1);
        else
            Environment.Exit(1);
    }
}
