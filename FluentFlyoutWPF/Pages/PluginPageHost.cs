using System.Windows;
using System.Windows.Controls;

namespace FluentFlyoutWPF.Pages;

public sealed class PluginPageHost : Page
{
    internal static Func<FrameworkElement>? CurrentFactory { get; set; }
    public PluginPageHost()
    {
        Content = CurrentFactory?.Invoke() ?? new TextBlock { Text = "Plugin page unavailable", Margin = new Thickness(24) };
    }
}
