using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using FluentFlyout.PluginApi;

namespace FluentFlyoutWPF.Classes.Plugins;

public sealed class PluginManager : IAsyncDisposable
{
    private static readonly NLog.Logger Logger = NLog.LogManager.GetCurrentClassLogger();
    private readonly List<LoadedPlugin> _loaded = [];
    private readonly Dictionary<string, bool> _disabled = new(StringComparer.OrdinalIgnoreCase);
    public static PluginManager Current { get; } = new();
    public IReadOnlyList<PluginManifest> LoadedPlugins => _loaded.Select(x => x.Manifest).ToList();
    public string PluginDirectory { get; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PulseFlyout", "Plugins");
    public IReadOnlyList<PluginPage> Pages => _loaded.SelectMany(x => x.Context.Pages).ToList();
    public IReadOnlyList<PluginTrayItem> TrayItems => _loaded.SelectMany(x => x.Context.TrayItems).ToList();

    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(PluginDirectory);
        var statePath = Path.Combine(PluginDirectory, "disabled.json");
        if (File.Exists(statePath))
        {
            try { var state = JsonSerializer.Deserialize<Dictionary<string, bool>>(await File.ReadAllTextAsync(statePath, cancellationToken)); if (state != null) foreach (var pair in state) _disabled[pair.Key] = pair.Value; } catch (Exception ex) { Logger.Warn(ex, "Could not read plugin state"); }
        }
        foreach (var directory in Directory.EnumerateDirectories(PluginDirectory)) if (!cancellationToken.IsCancellationRequested) await LoadDirectoryAsync(directory, cancellationToken);
    }

    public void SetEnabled(string id, bool enabled)
    {
        _disabled[id] = !enabled;
        File.WriteAllText(Path.Combine(PluginDirectory, "disabled.json"), JsonSerializer.Serialize(_disabled));
    }

    public async Task PublishMediaEventAsync(PluginMediaEvent mediaEvent)
    {
        foreach (var handler in _loaded.SelectMany(x => x.Context.MediaHandlers).ToList())
        {
            try { await handler(mediaEvent); } catch (Exception ex) { Logger.Error(ex, "Plugin media handler failed"); }
        }
    }

    private async Task LoadDirectoryAsync(string directory, CancellationToken cancellationToken)
    {
        try
        {
            var manifest = JsonSerializer.Deserialize<PluginManifest>(await File.ReadAllTextAsync(Path.Combine(directory, "plugin.json"), cancellationToken));
            if (manifest is null || string.IsNullOrWhiteSpace(manifest.Id) || _disabled.GetValueOrDefault(manifest.Id)) return;
            var loadContext = new AssemblyLoadContext($"PulseFlyout.Plugin.{manifest.Id}", true);
            var assembly = loadContext.LoadFromAssemblyPath(Path.GetFullPath(Path.Combine(directory, manifest.EntryAssembly)));
            if (Activator.CreateInstance(assembly.GetType(manifest.EntryType, true)!) is not IFluentFlyoutPlugin plugin) throw new InvalidOperationException("Entry type must implement IFluentFlyoutPlugin.");
            var context = new PluginContext(manifest, directory);
            await plugin.InitializeAsync(context, cancellationToken);
            _loaded.Add(new LoadedPlugin(manifest, plugin, context, loadContext));
            Logger.Info("Loaded plugin {0} {1}", manifest.Id, manifest.Version);
        }
        catch (Exception ex) { Logger.Error(ex, "Failed to load plugin from {0}", directory); }
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var item in _loaded.AsEnumerable().Reverse()) { try { await item.Plugin.ShutdownAsync(); } catch (Exception ex) { Logger.Error(ex, "Plugin shutdown failed: {0}", item.Manifest.Id); } item.LoadContext.Unload(); }
        _loaded.Clear();
    }
    private sealed record LoadedPlugin(PluginManifest Manifest, IFluentFlyoutPlugin Plugin, PluginContext Context, AssemblyLoadContext LoadContext);
    private sealed class PluginContext(PluginManifest manifest, string directory) : IPluginContext
    {
        private readonly Dictionary<string, Func<CancellationToken, Task>> _commands = new(StringComparer.OrdinalIgnoreCase);
        internal List<PluginPage> Pages { get; } = [];
        internal List<PluginTrayItem> TrayItems { get; } = [];
        internal List<Func<PluginMediaEvent, Task>> MediaHandlers { get; } = [];
        public PluginManifest Manifest { get; } = manifest;
        public string DataDirectory { get; } = Path.Combine(directory, "data");
        public void RegisterCommand(string id, Func<CancellationToken, Task> handler) => _commands[id] = handler;
        public bool TryExecuteCommand(string id, CancellationToken cancellationToken = default) { if (!_commands.TryGetValue(id, out var handler)) return false; _ = handler(cancellationToken); return true; }
        public void Log(string message, Exception? exception = null) => Logger.Info(exception, "[{0}] {1}", Manifest.Id, message);
        public void RegisterPage(PluginPage page) => Pages.Add(page);
        public void RegisterTrayItem(PluginTrayItem item) => TrayItems.Add(item);
        public void RegisterMediaEventHandler(Func<PluginMediaEvent, Task> handler) => MediaHandlers.Add(handler);
    }
}
