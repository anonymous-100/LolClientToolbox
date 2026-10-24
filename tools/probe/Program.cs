using System;
using System.IO;
using System.Linq;
using System.Reflection;

namespace Probe;

/// <summary>
/// LOL 客户端工具箱 · 发布前探针
///
/// 一条命令跑完全部离线检查，全过输出 PASS、退出码 0；任何一项失败退出码 1。
/// 用法（仓库根目录）：
///   dotnet run --project tools/probe -- [plugins目录]
/// plugins 目录缺省按 .dev/plugins → dist/plugins → publish/plugins 顺序找。
///
/// 检查项（对应 TODO 的「测试基建」）：
///   1. Live 接口解析（deathwatch）—— 真机抓的样本夹具，验证「认出自己 + 读 respawnTimer」
///   2. 插件依赖解析 —— 复刻宿主 ALC 规则加载全部插件（防 WinRT 投影缺 dll 回归）
///   3. SMTC 系统媒体会话 —— 播放/暂停的 WinRT 通路
///   4. 游戏音频会话 —— Core Audio COM 通路
///   5. 自动收尾判断 —— ShouldAutoClose 四组合（找到游戏窗口才跑全）
///   6. OCR 数字识别 —— 合成图走完整「像素→解码→识别」链路
///   7. GDI 抓屏 —— 像素长度 / 全黑检测 / 饱和度统计
///
/// 这套检查覆盖不了的部分（要对局才能验）：
///   · 真死了 respawnTimer 是不是倒计时口径（样本里只有别人死了）
///   · 真实死亡灰屏的饱和度落点、OCR 认不认得出游戏字体
///   · 切窗口本身的焦点行为（要物理前台）
/// </summary>
internal static class Program
{
    private static int _pass, _fail;

    private static void Main(string[] args)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        var root = FindPluginsRoot(args.FirstOrDefault());
        if (root is null)
        {
            Console.WriteLine("✕ 找不到 plugins 目录。先 dotnet publish 或用参数指定：");
            Console.WriteLine("  dotnet run --project tools/probe -- E:\\games\\LolClientToolbox\\.dev\\plugins");
            Environment.Exit(2);
        }

        Console.WriteLine($"== LOL 工具箱探针 · plugins = {root} ==");
        var plugins = Loader.LoadAll(root, s => Console.WriteLine("  " + s));
        Console.WriteLine();

        Run("Live 接口解析", () => Checks.LiveParse(Find(plugins, "deathwatch")));
        Run("插件依赖解析", () =>
        {
            var bad = plugins.Where(p => !Checks.DependencyResolve(p).Item1).ToList();
            return bad.Count == 0
                ? (true, $"{plugins.Count} 个插件全部可实例化（依赖齐全）")
                : (false, string.Join("；", bad.Select(p => Checks.DependencyResolve(p).Item2)));
        });
        Run("SMTC 媒体会话", () => Checks.Smtc(Find(plugins, "watchvideo")));
        Run("游戏音频会话", () => Checks.AudioMuteProbe(Find(plugins, "watchvideo")));
        Run("自动收尾判断", () =>
        {
            var (hwnd, label) = ProbeWin.FindGameLike();
            return Checks.ShouldAutoClose(Find(plugins, "watchvideo"), hwnd, label);
        });
        Run("OCR 数字识别", () => Checks.OcrDigits(Find(plugins, "deathwatch")));
        Run("GDI 抓屏", () => Checks.ScreenCapture(Find(plugins, "deathwatch")));

        Console.WriteLine();
        Console.WriteLine($"== 结果：{_pass} 过 / {_fail} 挂 ==");
        Environment.Exit(_fail == 0 ? 0 : 1);
    }

    private static void Run(string name, Func<(bool, string)> check)
    {
        bool ok;
        string note;
        try
        {
            var r = check();
            ok = r.Item1;
            note = r.Item2;
        }
        catch (Exception ex)
        {
            var inner = ex.InnerException ?? ex;
            (ok, note) = (false, "探针自身异常：" + inner.Message + " @ " + (inner.StackTrace ?? "").Split('\n').FirstOrDefault());
        }
        Console.WriteLine($"{(ok ? "✓" : "✕")} {name} —— {note}");
        if (ok) _pass++; else _fail++;
    }

    private static LoadedPlugin Find(System.Collections.Generic.IReadOnlyList<LoadedPlugin> ps, string id) =>
        ps.FirstOrDefault(p => p.Id == id)
        ?? throw new InvalidOperationException($"plugins/ 里没有 {id}");

    private static string? FindPluginsRoot(string? explicitRoot)
    {
        if (explicitRoot is not null)
            return Directory.Exists(explicitRoot) ? Path.GetFullPath(explicitRoot) : null;

        // 探针从 <repo>/tools/probe/bin/... 跑，向上找到仓库根再找候选目录
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "LolClientToolbox.sln")))
            dir = dir.Parent;
        if (dir is null) return null;

        foreach (var cand in new[] { ".dev/plugins", "dist/plugins", "publish/plugins" })
        {
            var p = Path.Combine(dir.FullName, cand);
            if (Directory.Exists(p)) return p;
        }
        return null;
    }
}
