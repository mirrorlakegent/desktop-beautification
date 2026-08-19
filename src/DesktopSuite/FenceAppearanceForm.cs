using System.Drawing;
using System.Windows.Forms;

namespace DesktopSuite;

/// <summary>
/// M4-B: fence box appearance editor. A modal WinForms dialog with <see cref="TableLayoutPanel"/>
/// + <see cref="AutoSize"/> so the layout adapts to any DPI / theme / TrackBar height.
/// "确定" commits (DialogResult.OK); "取消" discards (caller reverts live preview).
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
    private readonly TrackBar _frostOpacityTrack;
    private readonly Label _frostOpacityLabel;

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
        BackColor = Color.FromArgb(255, 28, 30, 38);
        ForeColor = Color.FromArgb(255, 220, 224, 232);
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;

        int width = 310; // trackbar/combobox width inside padding

        var tlp = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 1,
            RowCount = 0, // we add rows dynamically
            Padding = new Padding(16, 14, 16, 8),
        };
        tlp.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        Controls.Add(tlp);

        // Helper: append a row and return its index.
        int AddRow() { int r = tlp.RowCount++; tlp.RowStyles.Add(new RowStyle(SizeType.AutoSize)); return r; }

        // --- 圆角半径 ---
        int r = AddRow(); tlp.Controls.Add(MakeLabel("圆角半径"), 0, r);
        r = AddRow(); _cornerTrack = MakeTrack(width, 0, 40, _appearance.CornerRadius); tlp.Controls.Add(_cornerTrack, 0, r);
        r = AddRow(); _cornerLabel = MakeValueLabel($"{_appearance.CornerRadius} px"); tlp.Controls.Add(_cornerLabel, 0, r);

        // --- 主体透明度 ---
        r = AddRow(); tlp.Controls.Add(MakeLabel("主体透明度（越大越不透明）"), 0, r);
        r = AddRow(); _bodyTrack = MakeTrack(width, 0, 255, _appearance.BodyOpacity); tlp.Controls.Add(_bodyTrack, 0, r);
        r = AddRow(); _bodyLabel = MakeValueLabel($"{_appearance.BodyOpacity}"); tlp.Controls.Add(_bodyLabel, 0, r);

        // --- 标题栏透明度 ---
        r = AddRow(); tlp.Controls.Add(MakeLabel("标题栏透明度（越大越不透明）"), 0, r);
        r = AddRow(); _headerTrack = MakeTrack(width, 0, 255, _appearance.HeaderOpacity); tlp.Controls.Add(_headerTrack, 0, r);
        r = AddRow(); _headerLabel = MakeValueLabel($"{_appearance.HeaderOpacity}"); tlp.Controls.Add(_headerLabel, 0, r);

        // --- 标题字号 ---
        r = AddRow(); tlp.Controls.Add(MakeLabel("标题字号"), 0, r);
        r = AddRow(); _fontTrack = MakeTrack(width, 8, 32, (int)Math.Round(_appearance.TitleFontSize)); tlp.Controls.Add(_fontTrack, 0, r);
        r = AddRow(); _fontLabel = MakeValueLabel($"{_appearance.TitleFontSize:F0} px"); tlp.Controls.Add(_fontLabel, 0, r);

        // --- 标题对齐 ---
        r = AddRow(); tlp.Controls.Add(MakeLabel("标题对齐"), 0, r);
        _alignBox = new ComboBox
        {
            Width = width,
            DropDownStyle = ComboBoxStyle.DropDownList,
            BackColor = Color.FromArgb(255, 40, 44, 54),
            ForeColor = Color.FromArgb(255, 220, 224, 232)
        };
        _alignBox.Items.Add("左对齐");
        _alignBox.Items.Add("居中");
        _alignBox.SelectedIndex = _appearance.TitleAlign == 1 ? 1 : 0;
        _alignBox.SelectedIndexChanged += (_, _) => Fire();
        r = AddRow(); tlp.Controls.Add(_alignBox, 0, r);

        // --- 显示图标字形 ---
        _glyphBox = new CheckBox
        {
            Text = "显示分类图标（emoji 字形）",
            Width = width,
            Checked = _appearance.ShowGlyph,
            ForeColor = Color.FromArgb(255, 220, 224, 232)
        };
        _glyphBox.CheckedChanged += (_, _) => Fire();
        r = AddRow(); tlp.Controls.Add(_glyphBox, 0, r);

        // --- 毛玻璃 ---
        _frostBox = new CheckBox
        {
            Text = "毛玻璃背景（实验性，可能卡顿）",
            Width = width,
            Checked = _appearance.Frosted,
            ForeColor = Color.FromArgb(255, 220, 224, 232)
        };
        _frostBox.CheckedChanged += (_, _) => { UpdateFrostEnabled(); Fire(); };
        r = AddRow(); tlp.Controls.Add(_frostBox, 0, r);

        // --- 毛玻璃着色透明度 ---
        r = AddRow(); tlp.Controls.Add(MakeLabel("毛玻璃着色（越小越透）"), 0, r);
        r = AddRow(); _frostOpacityTrack = MakeTrack(width, 0, 200, _appearance.FrostOpacity); tlp.Controls.Add(_frostOpacityTrack, 0, r);
        r = AddRow(); _frostOpacityLabel = MakeValueLabel($"{_appearance.FrostOpacity}"); tlp.Controls.Add(_frostOpacityLabel, 0, r);

        // --- Buttons ---
        UpdateFrostEnabled();

        var btnPanel = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            FlowDirection = FlowDirection.RightToLeft,
            Padding = new Padding(0, 8, 16, 12),
            Height = 44
        };

        var ok = new Button
        {
            Text = "确定", Width = 80, Height = 32,
            DialogResult = DialogResult.OK,
            BackColor = Color.FromArgb(255, 40, 120, 200), ForeColor = Color.White
        };
        var cancel = new Button
        {
            Text = "取消", Width = 80, Height = 32, Margin = new Padding(0, 0, 8, 0),
            DialogResult = DialogResult.Cancel,
            BackColor = Color.FromArgb(255, 50, 54, 64), ForeColor = Color.White
        };
        btnPanel.Controls.Add(ok);
        btnPanel.Controls.Add(cancel);
        Controls.Add(btnPanel);
        AcceptButton = ok;
        CancelButton = cancel;

        // Wire live preview.
        _cornerTrack.ValueChanged += (_, _) => { _cornerLabel.Text = $"{_cornerTrack.Value} px"; Fire(); };
        _bodyTrack.ValueChanged += (_, _) => { _bodyLabel.Text = $"{_bodyTrack.Value}"; Fire(); };
        _headerTrack.ValueChanged += (_, _) => { _headerLabel.Text = $"{_headerTrack.Value}"; Fire(); };
        _fontTrack.ValueChanged += (_, _) => { _fontLabel.Text = $"{_fontTrack.Value} px"; Fire(); };
        _frostOpacityTrack.ValueChanged += (_, _) => { _frostOpacityLabel.Text = $"{_frostOpacityTrack.Value}"; Fire(); };

        _ready = true;
    }

    private void UpdateFrostEnabled()
    {
        bool on = _frostBox.Checked;
        _frostOpacityTrack.Enabled = on;
        _frostOpacityLabel.Enabled = on;
    }

    private static Label MakeLabel(string text) => new()
    {
        Text = text, AutoSize = true,
        ForeColor = Color.FromArgb(255, 200, 204, 212)
    };

    private static Label MakeValueLabel(string text) => new()
    {
        Text = text, AutoSize = true, TextAlign = ContentAlignment.TopRight,
        ForeColor = Color.FromArgb(255, 150, 210, 255), Anchor = AnchorStyles.Right
    };

    private static TrackBar MakeTrack(int width, int min, int max, int value) => new()
    {
        Width = width, AutoSize = false,
        Minimum = min, Maximum = max, Value = value,
        TickStyle = TickStyle.None
    };

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
        _appearance.FrostOpacity = _frostOpacityTrack.Value;
        _onPreview?.Invoke(_appearance.Clone());
    }
}
