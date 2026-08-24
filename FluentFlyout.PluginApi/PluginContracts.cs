using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;

namespace FluentFlyout.PluginApi;

public sealed record PluginManifest(string Id, string Name, string Version, string EntryAssembly, string EntryType, string? Description = null, string? Author = null);
public interface IFluentFlyoutPlugin
{
    Task InitializeAsync(IPluginContext context, CancellationToken cancellationToken = default);
    Task ShutdownAsync(CancellationToken cancellationToken = default);
}
public interface IPluginContext
{
    PluginManifest Manifest { get; }
    string DataDirectory { get; }
    void RegisterCommand(string id, Func<CancellationToken, Task> handler);
    bool TryExecuteCommand(string id, CancellationToken cancellationToken = default);
    void Log(string message, Exception? exception = null);
    void RegisterPage(PluginPage page);
    void RegisterTrayItem(PluginTrayItem item);
    void RegisterMediaEventHandler(Func<PluginMediaEvent, Task> handler);
}

public sealed record PluginPage(string Id, string Title, Func<FrameworkElement> CreateView);
public sealed record PluginTrayItem(string Id, string Header, Func<Task> Activate, string? ToolTip = null);
public sealed record PluginMediaEvent(
    string Kind,
    string? AppUserModelId = null,
    string? Title = null,
    string? Artist = null,
    string? LyricsText = null,
    string? LyricsFormat = null,
    long? PositionMs = null,
    long? DurationMs = null);
