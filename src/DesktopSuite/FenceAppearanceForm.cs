using System;
using System.Drawing;
using System.Windows.Forms;

namespace DesktopSuite;

/// <summary>
/// M4-B: fence box appearance editor.
/// <para>Fixed-size form with generous manual layout + inner scrollable panel as safety net.
/// Works reliably at any DPI because every control gets far more space than it needs.</para>
/// </summary>
public sealed class FenceAppearanceForm : Form
{
    private readonly FenceAppearance _appearance;
    private readonly Action<FenceAppearance> _onPreview;
    private bool _ready;

    private readonly TrackBar _cornerTrack;
    private readonly Label _cornerVal;
    private readonly TrackBar _bodyTrack;
    private readonly Label _bodyVal;
    private readonly TrackBar _headerTrack;
    private readonly Label _headerVal;
    private readonly TrackBar _fontTrack;
    private readonly Label _fontVal;
    private readonly ComboBox _alignBox;
    private readonly CheckBox _glyphBox;
    private readonly CheckBox _frostBox;
    private readonly TrackBar _frostOpacityTrack;
    private readonly Label _frostOpacityVal;

    public FenceAppearanceForm(FenceAppearance initial, Action<FenceAppearance> onPreview)
    {
        _appearance = initial.Clone();
        _onPreview = onPreview;

        // --- Form ---
        FormBorderStyle = FormBorderStyle.FixedDialog;
        ShowInTaskbar = false;
        TopMost = true;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        Text = "围栏外观";
        BackColor = Color.FromArgb(255, 28, 30, 38);
        ForeColor = Color.FromArgb(255, 220, 224, 232);
        ClientSize = new Size(440, 640);       // generous fixed size
        MinimumSize = new Size(420, 500);

        // Inner panel with auto-scroll as ultimate safety net for extreme DPI.
        var panel = new Panel
        {
            Dock = DockStyle.Fill,
            AutoScroll = true,
            Padding = new Padding(0),
        };
        Controls.Add(panel);

        int left = 16, ctrlW = 400;             // wide controls
        int y = 12;                              // cursor Y
        int labelH = 22, trackH = 36, gap = 6;   // generous per-row heights
        int sectionGap = 10;

        // ===== Helper lambdas =====
        Label AddLabel(string text)
        {
            var lbl = new Label
            {
                Text = text,
                Left = left,
                Top = y,
                Width = ctrlW,
                Height = labelH,
                AutoSize = false,
                ForeColor = Color.FromArgb(255, 200, 204, 212)
            };
            panel.Controls.Add(lbl);
            y += labelH;
            return lbl;
        }

        TrackBar AddTrack(int min, int max, int value)
        {
            var tb = new TrackBar
            {
                Left = left,
                Top = y,
                Width = ctrlW - 60,
                Height = trackH,
                Minimum = min,
                Maximum = max,
                Value = Math.Clamp(value, min, max),
                TickStyle = TickStyle.None
            };
            panel.Controls.Add(tb);
            y += trackH;
            return tb;
        }

        Label AddValueLabel(string text)
        {
            var lbl = new Label
            {
                Text = text,
                Left = left + ctrlW - 56,
                Top = y - trackH - 2,
                Width = 52,
                Height = labelH,
                AutoSize = false,
                TextAlign = ContentAlignment.MiddleRight,
                ForeColor = Color.FromArgb(255, 150, 210, 255)
            };
            panel.Controls.Add(lbl);
            return lbl;
        }

        void Skip(int px) { y += px; }

        // ===== 1. 圆角半径 =====
        AddLabel("圆角半径");
        _cornerTrack = AddTrack(0, 40, _appearance.CornerRadius);
        _cornerVal = AddValueLabel($"{_appearance.CornerRadius} px");
        Skip(gap);

        // ===== 2. 主体透明度 =====
        AddLabel("主体透明度（越大越不透明）");
        _bodyTrack = AddTrack(0, 255, _appearance.BodyOpacity);
        _bodyVal = AddValueLabel($"{_appearance.BodyOpacity}");
        Skip(gap);

        // ===== 3. 标题栏透明度 =====
        AddLabel("标题栏透明度（越大越不透明）");
        _headerTrack = AddTrack(0, 255, _appearance.HeaderOpacity);
        _headerVal = AddValueLabel($"{_appearance.HeaderOpacity}");
        Skip(gap);

        // ===== 4. 标题字号 =====
        AddLabel("标题字号");
        _fontTrack = AddTrack(8, 28, (int)Math.Round(Math.Clamp(_appearance.TitleFontSize, 8, 28)));
        _fontVal = AddValueLabel($"{Math.Clamp(_appearance.TitleFontSize, 8, 28):F0} px");
        Skip(gap);

        // ===== 5. 标题对齐 =====
        AddLabel("标题对齐");
        _alignBox = new ComboBox
        {
            Left = left,
            Top = y,
            Width = Math.Min(ctrlW, 200),
            DropDownStyle = ComboBoxStyle.DropDownList,
            BackColor = Color.FromArgb(255, 40, 44, 54),
            ForeColor = Color.FromArgb(255, 220, 224, 232)
        };
        _alignBox.Items.Add("左对齐");
        _alignBox.Items.Add("居中");
        _alignBox.SelectedIndex = _appearance.TitleAlign == 1 ? 1 : 0;
        _alignBox.SelectedIndexChanged += (_, _) => Fire();
        panel.Controls.Add(_alignBox);
        y += 28;
        Skip(gap);

        // ===== 6. 显示分类图标 =====
        _glyphBox = new CheckBox
        {
            Text = "显示分类图标（emoji 字形）",
            Left = left,
            Top = y,
            Width = ctrlW,
            Height = 26,
            Checked = _appearance.ShowGlyph,
            ForeColor = Color.FromArgb(255, 220, 224, 232)
        };
        _glyphBox.CheckedChanged += (_, _) => Fire();
        panel.Controls.Add(_glyphBox);
        y += 26;
        Skip(4);

        // ===== 7. 毛玻璃背景 =====
        _frostBox = new CheckBox
        {
            Text = "毛玻璃背景（实验性，可能卡顿）",
            Left = left,
            Top = y,
            Width = ctrlW,
            Height = 26,
            Checked = _appearance.Frosted,
            ForeColor = Color.FromArgb(255, 220, 224, 232)
        };
        _frostBox.CheckedChanged += (_, _) => { UpdateFrostEnabled(); Fire(); };
        panel.Controls.Add(_frostBox);
        y += 26;
        Skip(4);

        // ===== 8. 毛玻璃着色 =====
        AddLabel("毛玻璃着色（越小越透）");
        _frostOpacityTrack = AddTrack(0, 200, _appearance.FrostOpacity);
        _frostOpacityVal = AddValueLabel($"{_appearance.FrostOpacity}");
        Skip(sectionGap);

        // ===== 9. 按钮（放在 scrollable panel 内部最底部） =====
        var btnPanel = new FlowLayoutPanel
        {
            Left = left,
            Top = y,
            Width = ctrlW,
            Height = 44,
            FlowDirection = FlowDirection.RightToLeft,
            Padding = new Padding(0, 8, 0, 0)
        };

        var ok = new Button
        {
            Text = "确定",
            Width = 80,
            Height = 32,
            DialogResult = DialogResult.OK,
            BackColor = Color.FromArgb(255, 40, 120, 200),
            ForeColor = Color.White
        };
        var cancel = new Button
        {
            Text = "取消",
            Width = 80,
            Height = 32,
            Margin = new Padding(0, 0, 10, 0),
            DialogResult = DialogResult.Cancel,
            BackColor = Color.FromArgb(255, 50, 54, 64),
            ForeColor = Color.White
        };
        btnPanel.Controls.Add(ok);
        btnPanel.Controls.Add(cancel);
        panel.Controls.Add(btnPanel);

        AcceptButton = ok;
        CancelButton = cancel;

        // ===== Wire live preview =====
        _cornerTrack.ValueChanged += (_, _) => { _cornerVal.Text = $"{_cornerTrack.Value} px"; Fire(); };
        _bodyTrack.ValueChanged += (_, _) => { _bodyVal.Text = $"{_bodyTrack.Value}"; Fire(); };
        _headerTrack.ValueChanged += (_, _) => { _headerVal.Text = $"{_headerTrack.Value}"; Fire(); };
        _fontTrack.ValueChanged += (_, _) => { _fontVal.Text = $"{_fontTrack.Value} px"; Fire(); };
        _frostOpacityTrack.ValueChanged += (_, _) => { _frostOpacityVal.Text = $"{_frostOpacityTrack.Value}"; Fire(); };

        UpdateFrostEnabled();
        _ready = true;
    }

    private void UpdateFrostEnabled()
    {
        bool on = _frostBox.Checked;
        _frostOpacityTrack.Enabled = on;
        _frostOpacityVal.Enabled = on;
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
        _appearance.FrostOpacity = _frostOpacityTrack.Value;
        _onPreview?.Invoke(_appearance.Clone());
    }
}
