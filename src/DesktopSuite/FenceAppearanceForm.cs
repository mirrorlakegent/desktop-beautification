using System.Drawing;
using System.Windows.Forms;

namespace DesktopSuite;

/// <summary>
/// M4-B: fence box appearance editor. A small modal WinForms dialog (matching the existing
/// <c>VolumeForm</c> look) that exposes the seven user-tunable appearance properties and pushes
/// LIVE previews back through <paramref name="onPreview"/> on every change. "确定" commits
/// (DialogResult.OK); "取消" discards (the caller reverts the live preview).
/// </summary>
public sealed class FenceAppearanceForm : Form
{
    private readonly FenceAppearance _appearance;
    private readonly Action<FenceAppearance> _onPreview;
    private bool _ready;

    private readonly TrackBar _cornerTrack;
    private readonly Label _cornerLabel;
    private readonly TrackBar _bodyTrack;
    private readonly Label _bodyLabel;
    private readonly TrackBar _headerTrack;
    private readonly Label _headerLabel;
    private readonly TrackBar _fontTrack;
    private readonly Label _fontLabel;
    private readonly ComboBox _alignBox;
    private readonly CheckBox _glyphBox;
    private readonly CheckBox _frostBox;

    public FenceAppearanceForm(FenceAppearance initial, Action<FenceAppearance> onPreview)
    {
        _appearance = initial.Clone();
        _onPreview = onPreview;

        FormBorderStyle = FormBorderStyle.FixedDialog;
        ShowInTaskbar = false;
        TopMost = true;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        Text = "围栏外观";
        Width = 360;
        Height = 392;
        BackColor = Color.FromArgb(255, 28, 30, 38);
        ForeColor = Color.FromArgb(255, 220, 224, 232);

        int left = 16, width = 312, top = 14, rowH = 40;

        // --- 圆角半径 ---
        AddLabel("圆角半径", left, top);
        _cornerTrack = AddTrack(left, top + 16, width, 0, 40, _appearance.CornerRadius);
        _cornerLabel = AddValueLabel(left + width - 60, top, $"{_appearance.CornerRadius} px");
        top += rowH;

        // --- 主体透明度 ---
        AddLabel("主体透明度（越大越不透明）", left, top);
        _bodyTrack = AddTrack(left, top + 16, width, 0, 255, _appearance.BodyOpacity);
        _bodyLabel = AddValueLabel(left + width - 60, top, $"{_appearance.BodyOpacity}");
        top += rowH;

        // --- 标题栏透明度 ---
        AddLabel("标题栏透明度（越大越不透明）", left, top);
        _headerTrack = AddTrack(left, top + 16, width, 0, 255, _appearance.HeaderOpacity);
        _headerLabel = AddValueLabel(left + width - 60, top, $"{_appearance.HeaderOpacity}");
        top += rowH;

        // --- 标题字号 ---
        AddLabel("标题字号", left, top);
        _fontTrack = AddTrack(left, top + 16, width, 8, 32, (int)Math.Round(_appearance.TitleFontSize));
        _fontLabel = AddValueLabel(left + width - 60, top, $"{_appearance.TitleFontSize:F0} px");
        top += rowH;

        // --- 标题对齐 ---
        AddLabel("标题对齐", left, top);
        _alignBox = new ComboBox
        {
            Left = left,
            Top = top + 16,
            Width = width,
            DropDownStyle = ComboBoxStyle.DropDownList,
            BackColor = Color.FromArgb(255, 40, 44, 54),
            ForeColor = Color.FromArgb(255, 220, 224, 232)
        };
        _alignBox.Items.Add("左对齐");
        _alignBox.Items.Add("居中");
        _alignBox.SelectedIndex = _appearance.TitleAlign == 1 ? 1 : 0;
        _alignBox.SelectedIndexChanged += (_, _) => Fire();
        Controls.Add(_alignBox);
        top += rowH;

        // --- 显示图标字形 ---
        _glyphBox = new CheckBox
        {
            Text = "显示分类图标（emoji 字形）",
            Left = left,
            Top = top + 4,
            Width = width,
            Checked = _appearance.ShowGlyph,
            ForeColor = Color.FromArgb(255, 220, 224, 232)
        };
        _glyphBox.CheckedChanged += (_, _) => Fire();
        Controls.Add(_glyphBox);
        top += rowH - 8;

        // --- 毛玻璃（实验）---
        _frostBox = new CheckBox
        {
            Text = "毛玻璃背景（实验性，可能卡顿）",
            Left = left,
            Top = top + 4,
            Width = width,
            Checked = _appearance.Frosted,
            ForeColor = Color.FromArgb(255, 220, 224, 232)
        };
        _frostBox.CheckedChanged += (_, _) => Fire();
        Controls.Add(_frostBox);
        top += rowH - 8;

        // --- 按钮 ---
        var cancel = new Button
        {
            Text = "取消",
            Left = left + width - 168,
            Top = Height - 40,
            Width = 80,
            DialogResult = DialogResult.Cancel,
            BackColor = Color.FromArgb(255, 50, 54, 64),
            ForeColor = Color.White
        };
        var ok = new Button
        {
            Text = "确定",
            Left = left + width - 80,
            Top = Height - 40,
            Width = 80,
            DialogResult = DialogResult.OK,
            BackColor = Color.FromArgb(255, 40, 120, 200),
            ForeColor = Color.White
        };
        Controls.Add(cancel);
        Controls.Add(ok);
        AcceptButton = ok;
        CancelButton = cancel;

        // Wire live preview.
        _cornerTrack.ValueChanged += (_, _) => { _cornerLabel.Text = $"{_cornerTrack.Value} px"; Fire(); };
        _bodyTrack.ValueChanged += (_, _) => { _bodyLabel.Text = $"{_bodyTrack.Value}"; Fire(); };
        _headerTrack.ValueChanged += (_, _) => { _headerLabel.Text = $"{_headerTrack.Value}"; Fire(); };
        _fontTrack.ValueChanged += (_, _) => { _fontLabel.Text = $"{_fontTrack.Value} px"; Fire(); };

        _ready = true;
    }

    private Label AddLabel(string text, int left, int top)
    {
        var l = new Label
        {
            Text = text,
            Left = left,
            Top = top,
            Width = 280,
            ForeColor = Color.FromArgb(255, 200, 204, 212)
        };
        Controls.Add(l);
        return l;
    }

    private Label AddValueLabel(int left, int top, string text)
    {
        var l = new Label
        {
            Text = text,
            Left = left,
            Top = top,
            Width = 60,
            TextAlign = ContentAlignment.TopRight,
            ForeColor = Color.FromArgb(255, 150, 210, 255)
        };
        Controls.Add(l);
        return l;
    }

    private TrackBar AddTrack(int left, int top, int width, int min, int max, int value)
    {
        var t = new TrackBar
        {
            Left = left,
            Top = top,
            Width = width,
            Minimum = min,
            Maximum = max,
            Value = value,
            TickStyle = TickStyle.None
        };
        Controls.Add(t);
        return t;
    }

    private void Fire()
    {
        if (!_ready) return;
        _appearance.CornerRadius = _cornerTrack.Value;
        _appearance.BodyOpacity = _bodyTrack.Value;
        _appearance.HeaderOpacity = _headerTrack.Value;
        _appearance.TitleFontSize = _fontTrack.Value;
        _appearance.TitleAlign = _alignBox.SelectedIndex == 1 ? 1 : 0;
        _appearance.ShowGlyph = _glyphBox.Checked;
        _appearance.Frosted = _frostBox.Checked;
        _onPreview?.Invoke(_appearance.Clone());
    }
}
