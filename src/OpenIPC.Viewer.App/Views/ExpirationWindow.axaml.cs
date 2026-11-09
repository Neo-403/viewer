using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace OpenIPC.Viewer.App.Views;

public sealed partial class ExpirationWindow : Window
{
    public ExpirationWindow() => AvaloniaXamlLoader.Load(this);
}
