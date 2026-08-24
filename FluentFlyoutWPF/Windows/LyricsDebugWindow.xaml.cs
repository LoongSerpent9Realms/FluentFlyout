using System.Windows;
using System.Windows.Threading;
using FluentFlyoutWPF.Classes.Services;

namespace FluentFlyoutWPF.Windows;

public partial class LyricsDebugWindow : Window
{
    private readonly Func<string> _sessionInfo;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
    public LyricsDebugWindow(Func<string> sessionInfo)
    {
        InitializeComponent();
        _sessionInfo = sessionInfo;
        Refresh();
        _timer.Tick += (_, _) => Refresh();
        _timer.Start();
        Closed += (_, _) => _timer.Stop();
    }
    private void Refresh_Click(object sender, RoutedEventArgs e) => Refresh();
    private void Refresh() => DebugText.Text = $"{_sessionInfo()}\n\n请求地址:\n{LyricsService.LastRequestUrl}\n\n请求状态: {LyricsService.LastStatus}\n歌词原始长度: {LyricsService.LastLyricsLength}\n错误: {LyricsService.LastError}";
}
