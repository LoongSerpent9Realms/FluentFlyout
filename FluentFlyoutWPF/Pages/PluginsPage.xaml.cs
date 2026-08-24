using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows.Controls;
using System.Windows;
using FluentFlyoutWPF.Classes.Plugins;

namespace FluentFlyoutWPF.Pages;

public partial class PluginsPage : System.Windows.Controls.Page
{
    public PluginsPage()
    {
        InitializeComponent();
        PluginDirectoryText.Text = PluginManager.Current.PluginDirectory;
        var plugins = PluginManager.Current.LoadedPlugins;
        PluginList.ItemsSource = plugins;
        EmptyText.Visibility = plugins.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OpenPluginDirectory_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(PluginManager.Current.PluginDirectory);
            Process.Start(new ProcessStartInfo("explorer.exe", PluginManager.Current.PluginDirectory) { UseShellExecute = true });
        }
        catch { }
    }

    private void CreatePlugin_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new PluginCreateDialog { Owner = Window.GetWindow(this) };
        if (dialog.ShowDialog() != true) return;
        var id = SanitizeId(dialog.PluginIdText.Text);
        var name = dialog.PluginNameText.Text.Trim();
        if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(name)) return;
        var directory = Path.Combine(PluginManager.Current.PluginDirectory, id);
        Directory.CreateDirectory(directory);
        var className = ToPascal(id);
        var apiDll = Path.Combine(AppContext.BaseDirectory, "FluentFlyout.PluginApi.dll").Replace("\\", "\\\\");
        File.WriteAllText(Path.Combine(directory, "plugin.json"), $"{{\n  \"Id\": \"{id}\",\n  \"Name\": \"{name}\",\n  \"Version\": \"1.0.0\",\n  \"EntryAssembly\": \"{id}.dll\",\n  \"EntryType\": \"{className}.Entry\",\n  \"Author\": \"{dialog.AuthorText.Text.Trim()}\"\n}}", Encoding.UTF8);
        File.WriteAllText(Path.Combine(directory, $"{id}.csproj"), $"<Project Sdk=\"Microsoft.NET.Sdk\">\n  <PropertyGroup>\n    <TargetFramework>net10.0-windows10.0.22000.0</TargetFramework>\n    <UseWPF>true</UseWPF>\n    <Nullable>enable</Nullable>\n    <ImplicitUsings>enable</ImplicitUsings>\n  </PropertyGroup>\n  <ItemGroup><Reference Include=\"FluentFlyout.PluginApi\"><HintPath>{apiDll}</HintPath></Reference></ItemGroup>\n</Project>", Encoding.UTF8);
        File.WriteAllText(Path.Combine(directory, "Entry.cs"), $"using FluentFlyout.PluginApi;\nusing System.Windows;\nusing System.Windows.Controls;\n\nnamespace {className};\n\npublic sealed class Entry : IFluentFlyoutPlugin\n{{\n    public Task InitializeAsync(IPluginContext context, CancellationToken cancellationToken = default)\n    {{\n        context.RegisterPage(new PluginPage(\"{id}-page\", \"{name}\", () => new StackPanel {{ Margin = new Thickness(24), Children = {{ new TextBlock {{ Text = \"{name}\", FontSize = 24 }} }} }}));\n        return Task.CompletedTask;\n    }}\n    public Task ShutdownAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;\n}}", Encoding.UTF8);
        File.WriteAllText(Path.Combine(directory, "README.md"), $"# {name}\n\n自动生成的 PulseFlyout 插件项目。可以用 Visual Studio / VS Code 编辑，也可以让 AI 继续开发。\n\n编译 DLL 后重启 PulseFlyout。\n", Encoding.UTF8);
        try { Process.Start(new ProcessStartInfo("explorer.exe", directory) { UseShellExecute = true }); } catch { }
        MessageBox.Show($"插件项目已创建：\n{directory}\n\n你可以自行编辑，也可以让 AI 继续开发。", "插件项目已创建", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private static string SanitizeId(string value) => new string(value.Trim().ToLowerInvariant().Select(ch => char.IsLetterOrDigit(ch) || ch == '-' ? ch : '-').ToArray()).Trim('-');
    private static string ToPascal(string value) => string.Concat(value.Split('-', StringSplitOptions.RemoveEmptyEntries).Select(x => char.ToUpperInvariant(x[0]) + x[1..]));
}

internal sealed class PluginCreateDialog : Window
{
    internal TextBox PluginNameText { get; } = new();
    internal TextBox PluginIdText { get; } = new();
    internal TextBox AuthorText { get; } = new();
    public PluginCreateDialog()
    {
        Title = "创建插件项目"; Width = 430; Height = 270; WindowStartupLocation = WindowStartupLocation.CenterOwner; ResizeMode = ResizeMode.NoResize;
        var panel = new StackPanel { Margin = new Thickness(24) };
        AddField(panel, "插件名称", PluginNameText, "My Plugin"); AddField(panel, "插件 ID", PluginIdText, "my-plugin"); AddField(panel, "作者（可选）", AuthorText, "");
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0) };
        var cancel = new Button { Content = "取消", Padding = new Thickness(16, 6, 16, 6), Margin = new Thickness(0, 0, 8, 0) }; cancel.Click += (_, _) => DialogResult = false;
        var create = new Button { Content = "创建", Padding = new Thickness(16, 6, 16, 6), IsDefault = true }; create.Click += (_, _) => DialogResult = true;
        buttons.Children.Add(cancel); buttons.Children.Add(create); panel.Children.Add(buttons); Content = panel;
    }
    private static void AddField(Panel panel, string label, TextBox box, string placeholder) { panel.Children.Add(new TextBlock { Text = label, Margin = new Thickness(0, 0, 0, 4) }); box.Text = placeholder; box.Margin = new Thickness(0, 0, 0, 10); panel.Children.Add(box); }
}
