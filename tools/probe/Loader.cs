using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;

namespace Probe;

/// <summary>
/// 复刻宿主的插件加载规则（见 src/LolClientToolbox/PluginHost.cs 的 PluginLoadContext）：
/// 默认上下文里已有的（Contracts、BCL）复用，保证类型同一性；其余在插件目录里找。
/// 探针必须走同一条路，验证的才是「宿主环境里能不能用」。
/// </summary>
internal sealed class PluginLoadContext : AssemblyLoadContext
{
    private readonly string _dir;

    public PluginLoadContext(string dllPath) : base("probe:" + Path.GetFileNameWithoutExtension(dllPath))
        => _dir = Path.GetDirectoryName(Path.GetFullPath(dllPath))!;

    protected override Assembly? Load(AssemblyName name)
    {
        foreach (var a in AssemblyLoadContext.Default.Assemblies)
        {
            if (string.Equals(a.GetName().Name, name.Name, StringComparison.OrdinalIgnoreCase))
                return a;
        }
        var p = Path.Combine(_dir, name.Name + ".dll");
        return File.Exists(p) ? LoadFromAssemblyPath(p) : null;
    }
}

/// <summary>一个插件的 ALC + 程序集，供各项检查复用。</summary>
internal sealed class LoadedPlugin
{
    public required string Id { get; init; }
    public required string Dir { get; init; }
    public required AssemblyLoadContext Alc { get; init; }
    public required Assembly Asm { get; init; }

    public Type? FindType(string simpleName) =>
        Asm.GetTypes().FirstOrDefault(t => t.Name == simpleName);
}

/// <summary>加载 plugins/&lt;id&gt;/ 下所有插件；Contracts 先进默认上下文。</summary>
internal static class Loader
{
    public static List<LoadedPlugin> LoadAll(string pluginsRoot, Action<string> log)
    {
        var contracts = FindContracts(pluginsRoot);
        if (contracts is not null)
        {
            Assembly.LoadFrom(contracts);   // 先进默认 ALC —— 插件的 Load 会复用它
            log($"Contracts 已预载（默认上下文）：{contracts}");
        }
        else
        {
            log("⚠ 没找到 LolClientToolbox.Contracts.dll —— 插件解析不了契约，检查 plugins 根目录或先编译 src");
        }

        // WinRT 运行时是进程级单例（见 PluginHost.PreloadSharedRuntime 的注释），
        // 多个插件都带时必须只加载一份，否则第二份初始化直接炸
        foreach (var name in new[] { "Microsoft.Windows.SDK.NET.dll", "WinRT.Runtime.dll" })
        {
            var shortName = Path.GetFileNameWithoutExtension(name);
            if (AssemblyLoadContext.Default.Assemblies.Any(a =>
                    string.Equals(a.GetName().Name, shortName, StringComparison.OrdinalIgnoreCase)))
                continue;
            var found = Directory.GetFiles(pluginsRoot, name, SearchOption.AllDirectories).FirstOrDefault();
            if (found is not null)
            {
                Assembly.LoadFrom(found);
                log($"共享运行时已预载：{name}");
            }
        }

        var list = new List<LoadedPlugin>();
        foreach (var dir in Directory.GetDirectories(pluginsRoot).OrderBy(d => d, StringComparer.Ordinal))
        {
            var id = Path.GetFileName(dir);
            var dll = Path.Combine(dir, id + ".dll");
            // 目录名不一定等于程序集名（如 publish 的目录是插件 id，程序集也是 id），
            // 都找不到就翻目录里唯一的非依赖 dll
            if (!File.Exists(dll))
            {
                var cand = Directory.GetFiles(dir, "*.dll")
                    .Where(f => !Path.GetFileName(f).StartsWith("Microsoft.")
                             && Path.GetFileName(f) != "WinRT.Runtime.dll"
                             && Path.GetFileName(f) != "LolClientToolbox.Contracts.dll")
                    .ToList();
                if (cand.Count == 1) dll = cand[0];
            }
            if (!File.Exists(dll)) { log($"  跳过 {id}（没有程序集）"); continue; }

            try
            {
                var alc = new PluginLoadContext(dll);
                var asm = alc.LoadFromAssemblyPath(dll);
                list.Add(new LoadedPlugin { Id = id, Dir = dir, Alc = alc, Asm = asm });
                log($"  {id} ← {Path.GetFileName(dll)}（依赖 {Directory.GetFiles(dir, "*.dll").Length - 1} 个）");
            }
            catch (Exception ex)
            {
                log($"  ✕ {id} 加载失败：{ex.Message}");
            }
        }
        return list;
    }

    /// <summary>
    /// 找 Contracts.dll。发布目录是单文件 exe（Contracts 被合并进去了），
    /// 所以优先从源码的编译输出拿 —— 那正是插件 ALC 里「默认上下文那份」的同一来源。
    /// </summary>
    private static string? FindContracts(string pluginsRoot)
    {
        var parent = Path.GetDirectoryName(Path.GetFullPath(pluginsRoot));
        if (parent is not null)
        {
            var p = Path.Combine(parent, "LolClientToolbox.Contracts.dll");
            if (File.Exists(p)) return p;
        }

        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "LolClientToolbox.sln")))
            dir = dir.Parent;
        if (dir is null) return null;

        foreach (var cfg in new[] { "Release", "Debug" })
        {
            var p = Path.Combine(dir.FullName, "src", "LolClientToolbox.Contracts", "bin",
                                 cfg, "net9.0", "LolClientToolbox.Contracts.dll");
            if (File.Exists(p)) return p;
        }
        return null;
    }
}
