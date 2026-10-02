using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.Loader;
using PlattaPlayer.Codecs.Abstractions;
using PlattaPlayer.Data;
using PlattaPlayer.Visualizations.Abstractions;

namespace PlattaPlayer.App.Plugins;

/// <summary>What a plugins folder provided.</summary>
public sealed record LoadedPlugins(IReadOnlyList<IVisualizerPlugin> Visualizers, IReadOnlyList<ICodecPlugin> Codecs);

/// <summary>
/// Discovers plugins in a plugins folder — visualizers (<see cref="IVisualizerPlugin"/>) and codecs
/// (<see cref="ICodecPlugin"/>) — and instantiates every plugin type each assembly exports. Each candidate
/// assembly is loaded once, into its own <see cref="PluginLoadContext"/>, so a plugin's private dependencies
/// stay isolated, while the shared contract/framework assemblies (this app already loaded them) are reused so
/// types unify across the boundary. Loading is best-effort: a broken plugin is logged and skipped, never
/// crashing the app.
///
/// Layout: a plugin may be a single dll directly in the folder, or — recommended — its own subfolder
/// (with its private dependencies) whose main dll is named after the subfolder.
///
/// Security note: plugin code runs fully trusted, same as the app. Only drop in assemblies you trust.
/// </summary>
public static class PluginLoader
{
    public static LoadedPlugins LoadFrom(string directory)
    {
        var visualizers = new List<IVisualizerPlugin>();
        var codecs = new List<ICodecPlugin>();
        if (!Directory.Exists(directory))
            return new LoadedPlugins(visualizers, codecs);

        // The contract assemblies must be in the default context before any plugin is loaded: the load
        // context only shares assemblies the host has already loaded.
        _ = typeof(IVisualizerPlugin).Assembly;
        _ = typeof(ICodecPlugin).Assembly;

        foreach (var candidate in EnumerateCandidates(directory))
        {
            try
            {
                LoadCandidate(candidate, visualizers, codecs);
            }
            catch (Exception ex)
            {
                Log(candidate, ex);
            }
        }

        return new LoadedPlugins(visualizers, codecs);
    }

    // A plugin is either a dll sitting directly in the folder, or a subfolder whose main dll is named
    // after it (falling back to every top-level dll in that subfolder).
    private static IEnumerable<string> EnumerateCandidates(string directory)
    {
        foreach (var dll in Directory.EnumerateFiles(directory, "*.dll", SearchOption.TopDirectoryOnly))
            yield return dll;

        foreach (var subdir in Directory.EnumerateDirectories(directory))
        {
            var named = Path.Combine(subdir, Path.GetFileName(subdir) + ".dll");
            if (File.Exists(named))
            {
                yield return named;
            }
            else
            {
                foreach (var dll in Directory.EnumerateFiles(subdir, "*.dll", SearchOption.TopDirectoryOnly))
                    yield return dll;
            }
        }
    }

    private static void LoadCandidate(string assemblyPath, List<IVisualizerPlugin> visualizers, List<ICodecPlugin> codecs)
    {
        var context = new PluginLoadContext(assemblyPath);
        var assembly = context.LoadFromAssemblyPath(assemblyPath);

        foreach (var type in GetLoadableTypes(assembly))
        {
            if (type is null || type.IsAbstract || type.IsInterface) continue;
            var isVisualizer = typeof(IVisualizerPlugin).IsAssignableFrom(type);
            var isCodec = typeof(ICodecPlugin).IsAssignableFrom(type);
            if (!isVisualizer && !isCodec) continue;

            try
            {
                var instance = Activator.CreateInstance(type);
                if (instance is IVisualizerPlugin visualizer) visualizers.Add(visualizer);
                if (instance is ICodecPlugin codec) codecs.Add(codec);
            }
            catch (Exception ex)
            {
                // One plugin type failing to construct shouldn't take its siblings down with it.
                Log($"{assemblyPath} ({type.FullName})", ex);
            }
        }
    }

    // A dependency-only assembly can throw while resolving its references; keep whatever did load.
    private static IEnumerable<Type?> GetLoadableTypes(Assembly assembly)
    {
        try
        {
            return assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            return ex.Types;
        }
    }

    /// <summary>Appends a plugin failure to <c>plugins-error.log</c> in the data folder (best-effort).</summary>
    internal static void Log(string candidate, Exception ex)
    {
        try
        {
            var path = Path.Combine(AppPaths.DataDirectory, "plugins-error.log");
            File.AppendAllText(path, $"{DateTime.Now:o} {candidate}\n{ex}\n\n");
        }
        catch
        {
            // Logging is best-effort; never let a logging failure surface.
        }
    }
}

/// <summary>
/// Isolates a plugin's private dependencies while sharing anything the host has already loaded (the plugin
/// contracts, Core, Avalonia, the runtime) so types unify across the host/plugin boundary.
/// </summary>
internal sealed class PluginLoadContext : AssemblyLoadContext
{
    private readonly AssemblyDependencyResolver _resolver;

    public PluginLoadContext(string pluginPath)
        : base(name: Path.GetFileNameWithoutExtension(pluginPath), isCollectible: false)
        => _resolver = new AssemblyDependencyResolver(pluginPath);

    protected override Assembly? Load(AssemblyName assemblyName)
    {
        // Defer to the default context for any assembly the host already loaded, so shared contract and
        // framework types are the same Type instances on both sides.
        foreach (var loaded in Default.Assemblies)
            if (loaded.GetName().Name == assemblyName.Name)
                return null;

        var path = _resolver.ResolveAssemblyToPath(assemblyName);
        return path is null ? null : LoadFromAssemblyPath(path);
    }

    protected override IntPtr LoadUnmanagedDll(string unmanagedDllName)
    {
        var path = _resolver.ResolveUnmanagedDllToPath(unmanagedDllName);
        return path is null ? IntPtr.Zero : LoadUnmanagedDllFromPath(path);
    }
}
