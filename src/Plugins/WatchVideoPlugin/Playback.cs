using System;
using System.Linq;
using System.Threading.Tasks;
using Windows.Media.Control;

namespace WatchVideoPlugin;

/// <summary>
/// 视频播放状态检测 / 精确控制。
///
/// 为什么不用媒体键：媒体键是「播放/暂停」切换式，不知道自己当前是什么状态。
/// 用户手动暂停过视频再切走，程序会把它「切换」成播放 —— 结果就是切回游戏后
/// 后台还在放声音（用户实测反馈的问题）。
///
/// 改用 Windows 的系统媒体会话（SMTC）：Chrome/Edge 播放网页视频时会注册一个会话，
/// 能直接读到 PlaybackStatus，也能精确调用 TryPause / TryPlay。
/// 拿不到会话（浏览器没播、系统版本不支持）时返回 false，调用方回退到媒体键。
/// </summary>
internal static class Playback
{
    private static bool IsBrowserAumid(string aumid)
    {
        var a = (aumid ?? "").ToLowerInvariant();
        return a.Contains("chrome") || a.Contains("edge") || a.Contains("firefox")
               || a.Contains("brave") || a.Contains("opera") || a.Contains("vivaldi");
    }

    /// <summary>当前浏览器里的视频是不是在播放。拿不到会话返回 null。</summary>
    public static bool? IsPlaying()
    {
        try
        {
            var session = FindBrowserSession();
            if (session is null) return null;
            return session.GetPlaybackInfo().PlaybackStatus ==
                   GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
        }
        catch { return null; }
    }

    /// <summary>
    /// 精确设成「播放」或「暂停」：已经是目标状态就什么都不做。
    /// 成功返回 true；拿不到会话返回 false（调用方用媒体键兜底）。
    /// </summary>
    public static bool SetPlaying(bool play)
    {
        try
        {
            var session = FindBrowserSession();
            if (session is null) return false;

            var status = session.GetPlaybackInfo().PlaybackStatus;
            bool playing = status == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;

            // 已经是目标状态：不再动它 —— 这正是修「手动暂停后又被切回播放」的关键
            if (play == playing) return true;

            bool ok = play
                ? session.TryPlayAsync().AsTask().GetAwaiter().GetResult()
                : session.TryPauseAsync().AsTask().GetAwaiter().GetResult();
            return ok;
        }
        catch { return false; }
    }

    private static GlobalSystemMediaTransportControlsSession? FindBrowserSession()
    {
        var mgr = GlobalSystemMediaTransportControlsSessionManager
            .RequestAsync().AsTask().GetAwaiter().GetResult();
        return mgr.GetSessions().FirstOrDefault(s => IsBrowserAumid(s.SourceAppUserModelId));
    }
}
