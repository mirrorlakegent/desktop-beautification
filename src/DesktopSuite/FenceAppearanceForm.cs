using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace DesktopSuite;

/// <summary>
/// M4-B: fence box appearance editor.
/// <para>Fixed-size form with manual layout. Labels use AutoSize so Chinese text never truncates;
/// Y-cursor advances by actual measured heights. Dark title bar via DWM immersive mode.</para>
/// </summary>
public sealed class FenceAppearanceForm : Form
{
    private readonly FenceAppearance _appearance;
    private readonly Action<FenceAppearance> _onPreview;
    private bool _ready;

    private readonly TrackBar _cornerTrack;
    private Label? _cornerVal;
    private readonly TrackBar _bodyTrack;
    private Label? _bodyVal;
    private readonly TrackBar _headerTrack;
    private Label? _headerVal;
    private readonly TrackBar _fontTrack;
    private Label? _fontVal;
    private readonly ComboBox _alignBox;
    private readonly CheckBox _glyphBox;
    private readonly CheckBox _frostBox;
    private readonly TrackBar _frostOpacityTrack;
    private Label? _frostOpacityVal;

    // DWM dark title bar
    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref bool value, int size);

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
        Font = new Font("Microsoft YaHei UI", 9F);       // explicit CJK-capable UI font
        ClientSize = new Size(520, 660);
        MinimumSize = new Size(480, 520);

        // Inner scrollable panel as safety net
        var panel = new Panel
        {
            Dock = DockStyle.Fill,
            AutoScroll = true,
            Padding = new Padding(0),
        };
        Controls.Add(panel);

        int left = 16, ctrlW = 488;             // wide controls
        int y = 12;                              // cursor Y
        int trackH = 38, gap = 8;                // row spacing

        // ===== Helper: add auto-sizing label, advance Y by actual height =====
        void AddLabel(string text)
        {
            var lbl = new Label
            {
                Text = text,
                Left = left,
                Top = y,
                Width = ctrlW,
                AutoSize = true,
                UseCompatibleTextRendering = true,       // GDI engine for stable CJK at high DPI
                ForeColor = Color.FromArgb(255, 200, 204, 212)
            };
            panel.Controls.Add(lbl);
            y += lbl.Height + 2;                 // advance by rendered height + tiny gap
        }

        TrackBar AddTrack(int min, int max, int value)
        {
            var tb = new TrackBar
            {
                Left = left,
                Top = y,
                Width = ctrlW - 70,
                Height = trackH,
                Minimum = min,
                Maximum = max,
                Value = Math.Clamp(value, min, max),
                TickStyle = TickStyle.None
            };
            panel.Controls.Add(tb);
            y += trackH + 2;
            return tb;
        }

        void AddValueLabel(ref Label? field, string text)
        {
            field = new Label
            {
                Text = text,
                Left = left + ctrlW - 64,
                Top = y - trackH,
                Width = 58,
                Height = 24,
                AutoSize = false,
                TextAlign = ContentAlignment.MiddleRight,
                ForeColor = Color.FromArgb(255, 150, 210, 255)
            };
            panel.Controls.Add(field);
        }

        void Skip(int px) { y += px; }

        // ===== 1. 圆角半径 =====
        AddLabel("圆角半径");
        _cornerTrack = AddTrack(0, 40, _appearance.CornerRadius);
        AddValueLabel(ref _cornerVal, $"{_appearance.CornerRadius} px");
        Skip(gap);

        // ===== 2. 主体透明度 =====
        AddLabel("主体透明度（越大越不透明）");
        _bodyTrack = AddTrack(0, 255, _appearance.BodyOpacity);
        AddValueLabel(ref _bodyVal, $"{_appearance.BodyOpacity}");
        Skip(gap);

        // ===== 3. 标题栏透明度 =====
        AddLabel("标题栏透明度（越大越不透明）");
        _headerTrack = AddTrack(0, 255, _appearance.HeaderOpacity);
        AddValueLabel(ref _headerVal, $"{_appearance.HeaderOpacity}");
        Skip(gap);

        // ===== 4. 标题字号 =====
        AddLabel("标题字号");
        _fontTrack = AddTrack(8, 28, (int)Math.Round(Math.Clamp(_appearance.TitleFontSize, 8, 28)));
        AddValueLabel(ref _fontVal, $"{Math.Clamp(_appearance.TitleFontSize, 8, 28):F0} px");
        Skip(gap);

        // ===== 5. 标题对齐 =====
        AddLabel("标题对齐");
        _alignBox = new ComboBox
        {
            Left = left,
            Top = y,
            Width = 180,
            DropDownStyle = ComboBoxStyle.DropDownList,
            BackColor = Color.FromArgb(255, 40, 44, 54),
            ForeColor = Color.FromArgb(255, 220, 224, 232)
        };
        _alignBox.Items.Add("左对齐");
        _alignBox.Items.Add("居中");
        _alignBox.SelectedIndex = _appearance.TitleAlign == 1 ? 1 : 0;
        _alignBox.SelectedIndexChanged += (_, _) => Fire();
        panel.Controls.Add(_alignBox);
        y += _alignBox.Height + 2;
        Skip(gap);

        // ===== 6. 显示分类图标 =====
        _glyphBox = new CheckBox
        {
            Text = "显示分类图标（emoji 字形）",
            Left = left,
            Top = y,
            Width = ctrlW,
            AutoSize = true,                   // let checkbox size to fit text
            Checked = _appearance.ShowGlyph,
            ForeColor = Color.FromArgb(255, 220, 224, 232)
        };
        _glyphBox.CheckedChanged += (_, _) => Fire();
        panel.Controls.Add(_glyphBox);
        y += _glyphBox.Height + 2;
        Skip(4);

        // ===== 7. 毛玻璃背景 =====
        _frostBox = new CheckBox
        {
            Text = "毛玻璃背景（实验性，可能卡顿）",
            Left = left,
            Top = y,
            Width = ctrlW,
            AutoSize = true,
            Checked = _appearance.Frosted,
            ForeColor = Color.FromArgb(255, 220, 224, 232)
        };
        _frostBox.CheckedChanged += (_, _) => { UpdateFrostEnabled(); Fire(); };
        panel.Controls.Add(_frostBox);
        y += _frostBox.Height + 2;
        Skip(4);

        // ===== 8. 毛玻璃着色 =====
        AddLabel("毛玻璃着色（越小越透）");
        _frostOpacityTrack = AddTrack(0, 200, _appearance.FrostOpacity);
        AddValueLabel(ref _frostOpacityVal, $"{_appearance.FrostOpacity}");
        Skip(16);

        // ===== 9. 按钮 =====
        var btnPanel = new FlowLayoutPanel
        {
            Left = left,
            Top = y,
            Width = ctrlW,
            Height = 48,
            FlowDirection = FlowDirection.RightToLeft,
            Padding = new Padding(0, 12, 0, 0)
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
            Margin = new Padding(0, 0, 12, 0),
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
        _cornerTrack.ValueChanged += (_, _) => { _cornerVal!.Text = $"{_cornerTrack.Value} px"; Fire(); };
        _bodyTrack.ValueChanged += (_, _) => { _bodyVal!.Text = $"{_bodyTrack.Value}"; Fire(); };
        _headerTrack.ValueChanged += (_, _) => { _headerVal!.Text = $"{_headerTrack.Value}"; Fire(); };
        _fontTrack.ValueChanged += (_, _) => { _fontVal!.Text = $"{_fontTrack.Value} px"; Fire(); };
        _frostOpacityTrack.ValueChanged += (_, _) => { _frostOpacityVal!.Text = $"{_frostOpacityTrack.Value}"; Fire(); };

        UpdateFrostEnabled();
        _ready = true;

        // Apply dark title bar after handle is created (DWM needs valid HWND).
        Shown += (_, _) =>
        {
            bool dark = true;
            DwmSetWindowAttribute(Handle, DWMWA_USE_IMMERSIVE_DARK_MODE, ref dark, sizeof(bool));
        };
    }

    private void UpdateFrostEnabled()
    {
        bool on = _frostBox.Checked;
        _frostOpacityTrack.Enabled = on;
        _frostOpacityVal!.Enabled = on;
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
