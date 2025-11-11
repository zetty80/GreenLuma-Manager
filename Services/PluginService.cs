using System.IO;
using System.Runtime.Loader;
using System.Runtime.Serialization.Json;
using System.Text;
using GreenLuma_Manager.Models;
using GreenLuma_Manager.Plugins;

namespace GreenLuma_Manager.Services;

public class PluginService
{
    private static readonly string PluginsDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "GLM_Manager",
        "plugins");

    private static readonly string PluginsConfigPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "GLM_Manager",
        "plugins.json");

    private static readonly List<(PluginInfo Info, IPlugin? Instance, AssemblyLoadContext? Context)> LoadedPlugins = [];
    private static List<PluginInfo> _pluginInfos = [];

    public static void Initialize()
    {
        try
        {
            EnsurePluginsDirectoryExists();
            _pluginInfos = LoadPluginInfos();
            LoadPlugins();
        }
        catch
        {
            // ignored
        }
    }

    private static void EnsurePluginsDirectoryExists()
    {
        if (!Directory.Exists(PluginsDir)) Directory.CreateDirectory(PluginsDir);
    }

    private static List<PluginInfo> LoadPluginInfos()
    {
        try
        {
            if (!File.Exists(PluginsConfigPath)) return [];

            var json = File.ReadAllText(PluginsConfigPath, Encoding.UTF8);
            using var stream = new MemoryStream(Encoding.UTF8.GetBytes(json));
            var serializer = new DataContractJsonSerializer(typeof(List<PluginInfo>));
            return (List<PluginInfo>?)serializer.ReadObject(stream) ?? [];
        }
        catch
        {
            return [];
        }
    }

    private static void SavePluginInfos()
    {
        try
        {
            using var stream = new MemoryStream();
            var serializer = new DataContractJsonSerializer(typeof(List<PluginInfo>));
            serializer.WriteObject(stream, _pluginInfos);
            var json = Encoding.UTF8.GetString(stream.ToArray());
            File.WriteAllText(PluginsConfigPath, json, Encoding.UTF8);
        }
        catch
        {
            // ignored
        }
    }

    private static void LoadPlugins()
    {
        foreach (var pluginInfo in _pluginInfos.Where(p => p.IsEnabled))
            try
            {
                var pluginPath = Path.Combine(PluginsDir, pluginInfo.FileName);
                if (!File.Exists(pluginPath)) continue;

                var context = new AssemblyLoadContext($"Plugin_{pluginInfo.Id}", true);
                var assembly = context.LoadFromAssemblyPath(pluginPath);

                var pluginType = assembly.GetTypes()
                    .FirstOrDefault(t =>
                        typeof(IPlugin).IsAssignableFrom(t) && t is { IsInterface: false, IsAbstract: false });

                if (pluginType == null) continue;

                var instance = (IPlugin?)Activator.CreateInstance(pluginType);
                if (instance == null) continue;

                instance.Initialize();
                LoadedPlugins.Add((pluginInfo, instance, context));
            }
            catch
            {
                // ignored
            }
    }

    public static void OnApplicationStartup()
    {
        foreach (var (_, instance, _) in LoadedPlugins)
            try
            {
                instance?.OnApplicationStartup();
            }
            catch
            {
                // ignored
            }
    }

    public static void OnApplicationShutdown()
    {
        foreach (var (_, instance, context) in LoadedPlugins)
            try
            {
                instance?.OnApplicationShutdown();
                context?.Unload();
            }
            catch
            {
                // ignored
            }

        LoadedPlugins.Clear();
    }

    public static List<PluginInfo> GetAllPlugins()
    {
        return [.. _pluginInfos];
    }

    public static string ImportPlugin(string sourcePath)
    {
        try
        {
            if (!File.Exists(sourcePath)) return "Plugin file not found";

            var fileName = Path.GetFileName(sourcePath);
            var pluginId = Guid.NewGuid().ToString("N");
            var targetPath = Path.Combine(PluginsDir, $"{pluginId}_{fileName}");

            var manifest = ExtractManifest(sourcePath);
            if (manifest == null) return "Invalid plugin: Missing manifest or IPlugin implementation";

            if (_pluginInfos.Any(p => string.Equals(p.Name, manifest.Name, StringComparison.OrdinalIgnoreCase)))
                return $"Plugin '{manifest.Name}' is already installed";

            File.Copy(sourcePath, targetPath, true);

            var pluginInfo = new PluginInfo
            {
                Name = manifest.Name,
                Version = manifest.Version,
                Author = manifest.Author,
                Description = manifest.Description,
                FileName = Path.GetFileName(targetPath),
                IsEnabled = true,
                Id = pluginId
            };

            _pluginInfos.Add(pluginInfo);
            SavePluginInfos();

            return string.Empty;
        }
        catch (Exception ex)
        {
            return $"Import failed: {ex.Message}";
        }
    }

    private static PluginManifest? ExtractManifest(string assemblyPath)
    {
        AssemblyLoadContext? context = null;
        try
        {
            context = new AssemblyLoadContext(null, true);
            var assembly = context.LoadFromAssemblyPath(assemblyPath);

            var pluginType = assembly.GetTypes()
                .FirstOrDefault(t =>
                    typeof(IPlugin).IsAssignableFrom(t) && t is { IsInterface: false, IsAbstract: false });

            if (pluginType == null) return null;

            var instance = (IPlugin?)Activator.CreateInstance(pluginType);
            if (instance == null) return null;

            return new PluginManifest
            {
                Name = instance.Name,
                Version = instance.Version,
                Author = instance.Author,
                Description = instance.Description
            };
        }
        catch
        {
            return null;
        }
        finally
        {
            context?.Unload();
        }
    }

    public static void RemovePlugin(PluginInfo pluginInfo)
    {
        try
        {
            var pluginPath = Path.Combine(PluginsDir, pluginInfo.FileName);

            var loaded = LoadedPlugins.FirstOrDefault(p => p.Info.Id == pluginInfo.Id);
            if (loaded.Context != null)
            {
                try
                {
                    loaded.Instance?.OnApplicationShutdown();
                    loaded.Context.Unload();
                }
                catch
                {
                    // ignored
                }

                LoadedPlugins.Remove(loaded);
            }

            _pluginInfos.RemoveAll(p => p.Id == pluginInfo.Id);
            SavePluginInfos();

            if (File.Exists(pluginPath))
                try
                {
                    File.Delete(pluginPath);
                }
                catch
                {
                    // ignored
                }
        }
        catch
        {
            // ignored
        }
    }

    public static void TogglePlugin(PluginInfo pluginInfo, bool enabled)
    {
        try
        {
            var info = _pluginInfos.FirstOrDefault(p => p.Id == pluginInfo.Id);
            if (info == null) return;

            info.IsEnabled = enabled;
            SavePluginInfos();

            if (!enabled)
            {
                var loaded = LoadedPlugins.FirstOrDefault(p => p.Info.Id == pluginInfo.Id);
                if (loaded.Context != null)
                {
                    try
                    {
                        loaded.Instance?.OnApplicationShutdown();
                        loaded.Context.Unload();
                    }
                    catch
                    {
                        // ignored
                    }

                    LoadedPlugins.Remove(loaded);
                }
            }
        }
        catch
        {
            // ignored
        }
    }

    public static List<IPlugin> GetEnabledPlugins()
    {
        return
        [
            .. LoadedPlugins
                .Where(p => p.Instance != null)
                .Select(p => p.Instance!)
        ];
    }
}