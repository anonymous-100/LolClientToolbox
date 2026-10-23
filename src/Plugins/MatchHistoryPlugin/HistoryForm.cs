using System;
using System.Drawing;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace MatchHistoryPlugin;

/// <summary>
/// 战绩查询窗口。深色简陋风，不追求惊艳，追求信息密度和「一眼看懂」。
/// 上下两栏：上栏是对局列表，下栏是「当局所有人」计分板 —— 点上方一局，
/// 下方立即显示这局 10 个人的英雄/KDA/伤害，不用再双击找详情。
/// 所有网络访问都在 async 方法里，UI 线程不卡。
/// </summary>
internal sealed class HistoryForm : Form
{
    // 本地迷你调色板（插件不引用宿主的 Theme，自己说了算）
    private static readonly Color C_Bg = Color.FromArgb(20, 23, 29);
    private static readonly Color C_Panel = Color.FromArgb(31, 36, 44);
    private static readonly Color C_Text = Color.FromArgb(230, 234, 242);
    private static readonly Color C_Dim = Color.FromArgb(122, 132, 150);
    private static readonly Color C_Gold = Color.FromArgb(200, 164, 93);
    private static readonly Color C_Win = Color.FromArgb(95, 174, 140);
    private static readonly Color C_Lose = Color.FromArgb(221, 107, 96);

    private readonly MatchHistoryService _svc;
    private ComboBox _who = null!;
    private Button _refresh = null!;
    private Label _summary = null!;
    private ListView _list = null!;
    private ListView _score = null!;
    private Label _scoreHead = null!;
    private List<GameRow> _games = new();
    private readonly List<(string Puuid, string Label)> _recent = new();
    private int _scoreSeq;   // 计分板加载序号：连点多局时只认最后一次

    public HistoryForm(MatchHistoryService svc)
    {
        _svc = svc;
        Text = "查询战绩";
        ClientSize = new Size(780, 620);
        // 位置由宿主摆（IPluginHost.ShowAuxWindow → 主窗口右侧），这里不自己定位
        StartPosition = FormStartPosition.Manual;
        Location = new Point(-2000, -2000);   // 宿主摆好前的临时位，避免屏幕中间闪一下
        BackColor = C_Bg;
        ForeColor = C_Text;
        Font = new Font("Microsoft YaHei UI", 9f);
        MinimumSize = new Size(640, 480);

        // ---- 顶栏：查询对象 + 刷新 + 摘要 ----
        var top = new Panel { Dock = DockStyle.Top, Height = 44, BackColor = C_Panel, Padding = new Padding(10, 8, 10, 6) };
        _who = new ComboBox
        {
            DropDownStyle = ComboBoxStyle.DropDownList, Width = 300, BackColor = C_Bg,
            ForeColor = C_Text, FlatStyle = FlatStyle.Flat,
        };
        _refresh = new Button
        {
            Text = "刷新", Width = 72, FlatStyle = FlatStyle.Flat, BackColor = C_Panel,
            ForeColor = C_Text, Cursor = Cursors.Hand,
        };
        _refresh.FlatAppearance.BorderColor = C_Dim;
        _summary = new Label
        {
            AutoSize = false, Width = 350, ForeColor = C_Dim, Anchor = AnchorStyles.Right,
            TextAlign = ContentAlignment.MiddleRight,
        };
        _refresh.Left = 320;
        _summary.Left = 410;
        top.Controls.Add(_who);
        top.Controls.Add(_refresh);
        top.Controls.Add(_summary);

        // ---- 上栏：对局列表 ----
        var gamesPanel = new Panel { Dock = DockStyle.Top, Height = 300, BackColor = C_Bg, Padding = new Padding(10, 6, 10, 0) };
        var gamesHead = new Label
        {
            Text = "对局列表 · 点击任意一局，下方显示当局所有人", Dock = DockStyle.Top, Height = 22,
            ForeColor = C_Gold, Font = new Font("Microsoft YaHei UI", 9f, FontStyle.Bold),
        };
        _list = new ListView
        {
            Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, MultiSelect = false,
            GridLines = false, BackColor = C_Bg, ForeColor = C_Text, BorderStyle = BorderStyle.None,
        };
        _list.Columns.Add("时间", 110);
        _list.Columns.Add("模式", 100);
        _list.Columns.Add("英雄", 100);
        _list.Columns.Add("结果", 55);
        _list.Columns.Add("K/D/A", 100);
        _list.Columns.Add("伤害", 70);
        _list.Columns.Add("时长", 60);
        _list.SelectedIndexChanged += async (_, _) => await ShowScoreboardAsync();
        gamesPanel.Controls.Add(_list);
        gamesPanel.Controls.Add(gamesHead);

        // ---- 下栏：当局所有人计分板（占满剩余高度）----
        var scorePanel = new Panel { Dock = DockStyle.Fill, BackColor = C_Bg, Padding = new Padding(10, 6, 10, 10) };
        _scoreHead = new Label
        {
            Text = "当局所有人", Dock = DockStyle.Top, Height = 22,
            ForeColor = C_Gold, Font = new Font("Microsoft YaHei UI", 9f, FontStyle.Bold),
        };
        _score = new ListView
        {
            Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, GridLines = false,
            BackColor = C_Bg, ForeColor = C_Text, BorderStyle = BorderStyle.None,
        };
        _score.Columns.Add("阵营", 55);
        _score.Columns.Add("英雄", 100);
        _score.Columns.Add("玩家", 190);
        _score.Columns.Add("K/D/A", 95);
        _score.Columns.Add("伤害", 70);
        scorePanel.Controls.Add(_score);
        scorePanel.Controls.Add(_scoreHead);

        // Dock：Fill 先 Add 拿剩余空间；两个 Top 后 Add 的贴更上
        Controls.Add(scorePanel);
        Controls.Add(gamesPanel);
        Controls.Add(top);

        _refresh.Click += async (_, _) => await LoadAsync();
        Shown += async (_, _) => await InitAsync();
    }

    /// <summary>自检用：当前两栏的行数、计分板表头文字和窗口位置。</summary>
    internal (int Games, int Scores) Counts => (_list.Items.Count, _score.Items.Count);
    internal string ScoreHeadText => _scoreHead.Text;
    internal string WindowPos => $"({Location.X},{Location.Y}) {ClientSize.Width}×{ClientSize.Height}";

    /// <summary>打开窗口：填查询对象下拉框（自己 + 最近一起玩过的人），并自动查自己。</summary>
    private async Task InitAsync()
    {
        _refresh.Enabled = false;
        try
        {
            _recent.Clear();
            _recent.AddRange(await _svc.GetRecentSummonersAsync());

            _who.Items.Clear();
            _who.Items.Add("我自己");
            foreach (var (_, label) in _recent)
                _who.Items.Add(label);
            _who.SelectedIndex = 0;

            await LoadAsync();
        }
        catch (Exception ex)
        {
            _summary.ForeColor = C_Lose;
            _summary.Text = "初始化失败：" + ex.Message;
        }
        finally
        {
            _refresh.Enabled = true;
        }
    }

    private async Task LoadAsync()
    {
        if (_who.SelectedIndex < 0) return;
        _refresh.Enabled = false;
        _summary.ForeColor = C_Dim;
        _summary.Text = "加载中…";
        try
        {
            // 下拉框第 0 项是自己；其余按下标对应 _recent
            string puuid = _who.SelectedIndex == 0 ? "" : _recent[_who.SelectedIndex - 1].Puuid;
            string whoName = _who.SelectedItem?.ToString() ?? "";

            _games = await _svc.GetMatchesAsync(puuid);
            Fill(whoName);
        }
        catch (Exception ex)
        {
            _games = new List<GameRow>();
            _list.Items.Clear();
            _score.Items.Clear();
            _scoreHead.Text = "当局所有人";
            _summary.ForeColor = C_Lose;
            _summary.Text = "查询失败：" + ex.Message;
        }
        finally
        {
            _refresh.Enabled = true;
        }
    }

    private void Fill(string whoName)
    {
        _list.BeginUpdate();
        _list.Items.Clear();
        int win = 0;
        foreach (var g in _games)
        {
            if (g.Win) win++;
            var item = new ListViewItem(g.When.ToString("MM-dd HH:mm"));
            item.SubItems.Add(g.Mode);
            item.SubItems.Add(g.Champion);
            item.SubItems.Add(g.Win ? "胜利" : "失败");
            item.SubItems.Add($"{g.Kills}/{g.Deaths}/{g.Assists}");
            item.SubItems.Add(g.Damage.ToString());
            item.SubItems.Add($"{g.DurationSec / 60}:{g.DurationSec % 60:00}");
            item.ForeColor = g.Win ? C_Win : C_Lose;
            item.Tag = g;
            _list.Items.Add(item);
        }
        _list.EndUpdate();

        int n = _games.Count;
        double rate = n == 0 ? 0 : win * 100.0 / n;
        _summary.ForeColor = C_Dim;
        _summary.Text = n == 0
            ? $"{whoName}：没有可显示的对局"
            : $"{whoName} 近 {n} 局 · 胜 {win} · 负 {n - win} · 胜率 {rate:0}%";

        // 默认选中第一局，直接把这局 10 个人摆出来
        if (_list.Items.Count > 0) _list.Items[0].Selected = true;
        else _ = ShowScoreboardAsync();
    }

    /// <summary>
    /// 选中一局 → 下栏显示这局的所有人（胜方排前面）。
    /// 完整 10 人数据按 gameId 另拉一个端点（列表端点只带被查询者 1 人，实测 2026-10-24）。
    /// </summary>
    private async Task ShowScoreboardAsync()
    {
        if (_list.SelectedItems.Count == 0 || _list.SelectedItems[0].Tag is not GameRow g)
        {
            _scoreHead.Text = "当局所有人";
            _score.Items.Clear();
            return;
        }

        int seq = ++_scoreSeq;
        _scoreHead.Text = $"当局所有人 · {g.Mode} · {g.When:MM-dd HH:mm} · 加载中…";
        _score.Items.Clear();

        List<ScoreLine> rows;
        try
        {
            rows = await _svc.GetScoreboardAsync(g.GameId);
        }
        catch (Exception ex)
        {
            if (seq != _scoreSeq) return;   // 已经又点了别的局，这次的结果作废
            _scoreHead.Text = $"当局所有人 · {g.Mode} · 加载失败：{ex.Message}";
            return;
        }
        if (seq != _scoreSeq) return;       // 连点保护：只渲染最后一次选中的局

        _score.BeginUpdate();
        _score.Items.Clear();
        // 胜方优先，然后按原始顺序；没数据的行不占位
        foreach (var s in rows
                 .Where(s => s.Player.Length > 0 || s.Champion.Length > 0)
                 .OrderByDescending(s => s.Win)
                 .ThenBy(s => s.Player))
        {
            var item = new ListViewItem(s.Win ? "胜方" : "负方");
            item.SubItems.Add(s.Champion);
            item.SubItems.Add(s.Player);
            item.SubItems.Add($"{s.K}/{s.D}/{s.A}");
            item.SubItems.Add(s.Damage.ToString());
            item.ForeColor = s.Win ? C_Win : C_Lose;
            _score.Items.Add(item);
        }
        _score.EndUpdate();

        _scoreHead.Text = rows.Count == 0
            ? $"当局所有人 · {g.Mode} · 这局没有玩家数据"
            : $"当局所有人 · {g.Mode} · {g.When:MM-dd HH:mm} · {rows.Count} 人 · {(g.Win ? "胜利" : "失败")}";
    }
}
