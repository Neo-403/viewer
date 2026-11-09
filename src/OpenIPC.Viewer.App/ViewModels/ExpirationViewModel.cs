using System;
using System.Globalization;
using System.Windows.Input;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using CommunityToolkit.Mvvm.Input;
using OpenIPC.Viewer.App.Services;
using OpenIPC.Viewer.Core;

namespace OpenIPC.Viewer.App.ViewModels;

/// <summary>
/// The only view model a time-limited build past its expiry ever creates: no IoC
/// services, no database, no cameras — just the notice and an exit button.
/// </summary>
public sealed class ExpirationViewModel
{
    public ExpirationViewModel()
    {
        var at = BuildExpiration.ExpiresAtText;
        ExpiresAtText = at is null
            ? null
            : string.Format(CultureInfo.CurrentCulture,
                Localizer.Instance["Expiration.ExpiresAtFormat"], at);
        ExitCommand = new RelayCommand(Exit);
    }

    public string Title => Localizer.Instance["Expiration.Title"];
    public string Message => Localizer.Instance["Expiration.Message"];
    public string ExitText => Localizer.Instance["Expiration.Exit"];
    public string? ExpiresAtText { get; }
    public bool HasExpiresAtText => ExpiresAtText is not null;
    public ICommand ExitCommand { get; }

    private static void Exit()
    {
        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            desktop.Shutdown(1);
        else
            Environment.Exit(1);
    }
}
