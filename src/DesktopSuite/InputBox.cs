using System.Windows.Forms;
using System.Drawing;

namespace DesktopSuite;

/// <summary>Minimal modal text-input dialog (no VB reference needed). Used for naming a new
/// appearance preset from the tray / appearance form.</summary>
public sealed class InputBox : Form
{
    private readonly TextBox _box;

    public static string? Show(string title, string prompt, string defaultValue = "")
    {
        using var dlg = new InputBox(title, prompt, defaultValue);
        return dlg.ShowDialog() == DialogResult.OK ? dlg._box.Text.Trim() : null;
    }

    private InputBox(string title, string prompt, string defaultValue)
    {
        Text = title;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        ShowInTaskbar = false;
        TopMost = true;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Color.FromArgb(255, 28, 30, 38);
        ForeColor = Color.FromArgb(255, 220, 224, 232);
        Font = new Font("Microsoft YaHei UI", 9F);
        ClientSize = new Size(360, 130);
        AutoScaleMode = AutoScaleMode.Dpi;

        var lbl = new Label
        {
            Text = prompt,
            Left = 12, Top = 12, Width = 336, AutoSize = false, Height = 36,
            UseCompatibleTextRendering = true,
            ForeColor = Color.FromArgb(255, 200, 204, 212)
        };
        _box = new TextBox
        {
            Left = 12, Top = 54, Width = 336, Text = defaultValue,
            BackColor = Color.FromArgb(255, 40, 44, 54),
            ForeColor = Color.White
        };
        var ok = new Button
        {
            Text = "确定", DialogResult = DialogResult.OK,
            Left = 204, Top = 92, Width = 72, Height = 28,
            BackColor = Color.FromArgb(255, 40, 120, 200), ForeColor = Color.White
        };
        var cancel = new Button
        {
            Text = "取消", DialogResult = DialogResult.Cancel,
            Left = 284, Top = 92, Width = 64, Height = 28,
            BackColor = Color.FromArgb(255, 50, 54, 64), ForeColor = Color.White
        };
        AcceptButton = ok;
        CancelButton = cancel;
        Controls.Add(lbl);
        Controls.Add(_box);
        Controls.Add(ok);
        Controls.Add(cancel);
        _box.SelectAll();
    }
}
