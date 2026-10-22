using System.Drawing;
using System.Windows.Forms;

namespace MatchHistoryPlugin;

/// <summary>
/// 战绩查询窗口。深色简陋风，不追求惊艳，追求信息密度和「一眼看懂」。
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
    private List<GameRow> _games = new();
    private readonly List<(string Puuid, string Label)> _recent = new();

    public HistoryForm(MatchHistoryService svc)
    {
        _svc = svc;
        Text = "查询战绩";
        ClientSize = new Size(780, 560);
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = C_Bg;
        ForeColor = C_Text;
        Font = new Font("Microsoft YaHei UI", 9f);
        MinimumSize = new Size(640, 400);

        // ---- 顶栏 ----
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
            AutoSize = true, ForeColor = C_Dim, Anchor = AnchorStyles.Right,
            TextAlign = ContentAlignment.MiddleRight,
        };
        // 简单横排：下拉框在左，摘要在右，刷新在下拉框右边
        _refresh.Left = 320;
        _summary.Left = 410;
        _summary.Width = 350;
        top.Controls.Add(_who);
        top.Controls.Add(_refresh);
        top.Controls.Add(_summary);

        // ---- 列表 ----
        _list = new ListView
        {
            Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, MultiSelect = false,
            GridLines = false, BackColor = C_Bg, ForeColor = C_Text, BorderStyle = BorderStyle.None,
        };
        _list.Columns.Add("时间", 120);
        _list.Columns.Add("模式", 110);
        _list.Columns.Add("英雄", 110);
        _list.Columns.Add("结果", 60);
        _list.Columns.Add("K/D/A", 110);
        _list.Columns.Add("伤害", 80);
        _list.Columns.Add("时长", 70);
        _list.MouseDoubleClick += (_, _) => ShowDetail();

        Controls.Add(_list);
        Controls.Add(top);

        _refresh.Click += async (_, _) => await LoadAsync();
        Shown += async (_, _) => await InitAsync();
    }

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
            string puuid = _who.SelectedIndex == 0
                ? ""
                : _recent[_who.SelectedIndex - 1].Puuid;

            _games = await _svc.GetMatchesAsync(puuid);
            Fill();
        }
        catch (Exception ex)
        {
            _games = new List<GameRow>();
            _list.Items.Clear();
            _summary.ForeColor = C_Lose;
            _summary.Text = "查询失败：" + ex.Message;
        }
        finally
        {
            _refresh.Enabled = true;
        }
    }

    private void Fill()
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
        _summary.Text = $"共 {n} 场 · 胜 {win} · 负 {n - win} · 胜率 {rate:0}%";
    }

    /// <summary>双击一行：看这局的完整计分板。</summary>
    private void ShowDetail()
    {
        if (_list.SelectedItems.Count == 0) return;
        if (_list.SelectedItems[0].Tag is not GameRow g) return;

        using var f = new Form
        {
            Text = $"{g.Mode} · {g.When:yyyy-MM-dd HH:mm} · {(g.Win ? "胜利" : "失败")}",
            ClientSize = new Size(560, 480),
            StartPosition = FormStartPosition.CenterParent,
            BackColor = C_Bg, ForeColor = C_Text,
            Font = new Font("Microsoft YaHei UI", 9f),
        };
        var lv = new ListView
        {
            Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, GridLines = false,
            BackColor = C_Bg, ForeColor = C_Text, BorderStyle = BorderStyle.None,
        };
        lv.Columns.Add("阵营", 60);
        lv.Columns.Add("英雄", 110);
        lv.Columns.Add("玩家", 180);
        lv.Columns.Add("K/D/A", 100);
        lv.Columns.Add("伤害", 70);

        foreach (var s in g.Scoreboard)
        {
            var item = new ListViewItem(s.Win ? "胜方" : "负方");
            item.SubItems.Add(s.Champion);
            item.SubItems.Add(s.Player);
            item.SubItems.Add($"{s.K}/{s.D}/{s.A}");
            item.SubItems.Add(s.Damage.ToString());
            item.ForeColor = s.Win ? C_Win : C_Lose;
            if (s.Player.Length == 0 && s.Champion.Length == 0) continue;
            lv.Items.Add(item);
        }
        f.Controls.Add(lv);
        f.ShowDialog(this);
    }
}
