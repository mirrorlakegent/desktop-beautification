using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace DesktopSuite;

/// <summary>
/// M4-B: fence box appearance editor.
/// <para>Manual layout where the Y-cursor advances by each control's REAL <c>Bottom</c> (never a
/// guessed fixed height) — this is the key fix for the recurring "slider pushes the next row / the
/// value text is truncated" bug on high-DPI systems where TrackBar is taller than 38px.</para>
/// <para>Labels use AutoSize so Chinese text never truncates. Dark title bar via DWM immersive mode.
/// (No GDI+ here — Form text uses the GDI engine via UseCompatibleTextRendering for stable CJK.)</para>
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
        AutoScaleMode = AutoScaleMode.Dpi;               // scale controls to the user's DPI
        AutoScaleDimensions = new SizeF(96F, 96F);
        ClientSize = new Size(560, 720);
        MinimumSize = new Size(500, 560);

        // Inner scrollable panel as safety net for extreme DPI.
        var panel = new Panel
        {
            Dock = DockStyle.Fill,
            AutoScroll = true,
            Padding = new Padding(0)
        };
        Controls.Add(panel);

        int left = 16, ctrlW = 528;        // wide controls (fits 560 - 2*16)
        int y = 12;                        // cursor Y, advanced by REAL control bottoms

        // ===== Helper: auto-sizing label; advance y by its REAL bottom =====
        void AddLabel(string text)
        {
            var lbl = new Label
            {
                Text = text,
                Left = left,
                Top = y,
                AutoSize = true,
                MaximumSize = new Size(ctrlW, 0),          // wrap instead of overflow if very long
                UseCompatibleTextRendering = true,          // GDI engine: stable CJK at high DPI
                ForeColor = Color.FromArgb(255, 200, 204, 212)
            };
            panel.Controls.Add(lbl);
            y = lbl.Bottom + 2;                              // <- real height, not a guess
        }

        // ===== Helper: slider; advance y by its REAL bottom (fixes the overlap bug) =====
        TrackBar AddTrack(int min, int max, int value, out int rowTop)
        {
            var tb = new TrackBar
            {
                Left = left,
                Top = y,
                Width = ctrlW - 76,                          // leave room for the value label on the right
                Minimum = min,
                Maximum = max,
                Value = Math.Clamp(value, min, max),
                TickStyle = TickStyle.None
            };
            panel.Controls.Add(tb);
            rowTop = y;                                      // top of this slider row
            y = tb.Bottom + 4;                               // <- real height, not a fixed 38px
            return tb;
        }

        // ===== Helper: value label sitting on the right of a slider row, vertically centered =====
        void AddVal(ref Label? field, string text, int rowTop)
        {
            field = new Label
            {
                Text = text,
                Left = left + ctrlW - 70,
                AutoSize = true,                             // never truncate the number
                TextAlign = ContentAlignment.MiddleRight,
                UseCompatibleTextRendering = true,
                ForeColor = Color.FromArgb(255, 150, 210, 255)
            };
            panel.Controls.Add(field);
            // Vertically center the value next to the (taller) slider.
            field.Top = rowTop + Math.Max(0, (24 - field.Height) / 2) + 6;
        }

        void Skip(int px) { y += px; }

        // ===== 1. 圆角半径 =====
        AddLabel("圆角半径");
        _cornerTrack = AddTrack(0, 40, _appearance.CornerRadius, out _);
        AddVal(ref _cornerVal, $"{_appearance.CornerRadius} px", _cornerTrack.Top);
        Skip(8);

        // ===== 2. 主体透明度 =====
        AddLabel("主体透明度（越大越不透明）");
        _bodyTrack = AddTrack(0, 255, _appearance.BodyOpacity, out _);
        AddVal(ref _bodyVal, $"{_appearance.BodyOpacity}", _bodyTrack.Top);
        Skip(8);

        // ===== 3. 标题栏透明度 =====
        AddLabel("标题栏透明度（越大越不透明）");
        _headerTrack = AddTrack(0, 255, _appearance.HeaderOpacity, out _);
        AddVal(ref _headerVal, $"{_appearance.HeaderOpacity}", _headerTrack.Top);
        Skip(8);

        // ===== 4. 标题字号 =====
        AddLabel("标题字号");
        int fontVal = (int)Math.Round(Math.Clamp(_appearance.TitleFontSize, 8, 28));
        _fontTrack = AddTrack(8, 28, fontVal, out _);
        AddVal(ref _fontVal, $"{fontVal} px", _fontTrack.Top);
        Skip(8);

        // ===== 5. 标题对齐 =====
        AddLabel("标题对齐");
        _alignBox = new ComboBox
        {
            Left = left,
            Top = y,
            Width = 200,
            DropDownStyle = ComboBoxStyle.DropDownList,
            BackColor = Color.FromArgb(255, 40, 44, 54),
            ForeColor = Color.FromArgb(255, 220, 224, 232)
        };
        _alignBox.Items.Add("左对齐");
        _alignBox.Items.Add("居中");
        _alignBox.SelectedIndex = _appearance.TitleAlign == 1 ? 1 : 0;
        _alignBox.SelectedIndexChanged += (_, _) => Fire();
        panel.Controls.Add(_alignBox);
        y = _alignBox.Bottom + 4;
        Skip(8);

        // ===== 6. 显示分类图标 =====
        _glyphBox = new CheckBox
        {
            Text = "显示分类图标（emoji 字形）",
            Left = left,
            Top = y,
            AutoSize = true,
            Checked = _appearance.ShowGlyph,
            ForeColor = Color.FromArgb(255, 220, 224, 232),
            UseCompatibleTextRendering = true
        };
        _glyphBox.CheckedChanged += (_, _) => Fire();
        panel.Controls.Add(_glyphBox);
        y = _glyphBox.Bottom + 4;
        Skip(4);

        // ===== 7. 毛玻璃背景 =====
        _frostBox = new CheckBox
        {
            Text = "毛玻璃背景（模糊当前桌面壁纸）",
            Left = left,
            Top = y,
            AutoSize = true,
            Checked = _appearance.Frosted,
            ForeColor = Color.FromArgb(255, 220, 224, 232),
            UseCompatibleTextRendering = true
        };
        _frostBox.CheckedChanged += (_, _) => { UpdateFrostEnabled(); Fire(); };
        panel.Controls.Add(_frostBox);
        y = _frostBox.Bottom + 4;
        Skip(4);

        // ===== 8. 毛玻璃着色 =====
        AddLabel("毛玻璃着色（越小越透）");
        _frostOpacityTrack = AddTrack(0, 200, _appearance.FrostOpacity, out _);
        AddVal(ref _frostOpacityVal, $"{_appearance.FrostOpacity}", _frostOpacityTrack.Top);
        Skip(20);

        // ===== 9. 按钮 =====
        y += 4;
        var btnPanel = new FlowLayoutPanel
        {
            Left = left,
            Top = y,
            Width = ctrlW,
            Height = 44,
            FlowDirection = FlowDirection.RightToLeft,
            Padding = new Padding(0, 10, 0, 0)
        };

        var ok = new Button
        {
            Text = "确定",
            Width = 84,
            Height = 32,
            DialogResult = DialogResult.OK,
            BackColor = Color.FromArgb(255, 40, 120, 200),
            ForeColor = Color.White
        };
        var cancel = new Button
        {
            Text = "取消",
            Width = 84,
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

        // Apply dark title bar after handle is created (DWM needs a valid HWND).
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
