using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using System.IO;
using System.Drawing.Imaging;

internal static class Program
{
    [STAThread]
    static void Main()
    {
        ApplicationConfiguration.Initialize();
        Application.Run(new TrayHost());
    }
}

/* ---------- Clipboard Helper: sağlam kopyalama + retry ---------- */
static class ClipboardHelper
{
    public static bool TrySetImage(Bitmap src, int retries = 8, int delayMs = 80)
    {
        for (int i = 0; i < retries; i++)
        {
            try
            {
                using var bmp = new Bitmap(src.Width, src.Height, PixelFormat.Format32bppArgb);
                using (var g = Graphics.FromImage(bmp)) g.DrawImage(src, 0, 0);

                var dobj = new DataObject();
                dobj.SetImage((Bitmap)bmp.Clone());

                Clipboard.Clear();
                Clipboard.SetDataObject(dobj, true, 10, 50);
                return true;
            }
            catch
            {
                System.Threading.Thread.Sleep(delayMs);
            }
        }
        return false;
    }
}

/* ---------- Tray host: global hotkey + tray menü ---------- */
public class TrayHost : Form
{
    [DllImport("user32.dll")] private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);
    [DllImport("user32.dll")] private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    private const int WM_HOTKEY = 0x0312;
    private const int HOTKEY_ID = 1;
    private const uint MOD_CONTROL = 0x0002;
    private const uint MOD_SHIFT = 0x0004;

    private readonly NotifyIcon _tray;

    public TrayHost()
    {
        ShowInTaskbar = false;
        Opacity = 0;
        Width = Height = 0;

        _tray = new NotifyIcon
        {
            Visible = true,
            Text = "SnipMini – Ctrl+Shift+S ile yakala",
            Icon = SystemIcons.Application
        };
        var menu = new ContextMenuStrip();
        menu.Items.Add("Ekran görüntüsü al (Ctrl+Shift+S)", null, (s, e) => StartSnip());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Çıkış", null, (s, e) => Close());
        _tray.ContextMenuStrip = menu;
        _tray.DoubleClick += (s, e) => StartSnip();
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        if (!RegisterHotKey(Handle, HOTKEY_ID, MOD_CONTROL | MOD_SHIFT, (uint)Keys.S))
        {
            _tray.ShowBalloonTip(3000, "SnipMini",
                "Ctrl+Shift+S kısayolu kaydedilemedi (başka bir uygulama kullanıyor olabilir). Tray menüsünden çalıştırabilirsiniz.",
                ToolTipIcon.Warning);
        }
    }

    protected override void OnHandleDestroyed(EventArgs e)
    {
        UnregisterHotKey(Handle, HOTKEY_ID);
        base.OnHandleDestroyed(e);
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _tray.Visible = false;
        _tray.Dispose();
        base.OnFormClosed(e);
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WM_HOTKEY && m.WParam.ToInt32() == HOTKEY_ID)
            StartSnip();

        base.WndProc(ref m);
    }

    private void StartSnip()
    {
        while (true)
        {
            // Her denemede yeni overlay (retry/odak sorunlarını azaltır)
            using var overlay = new OverlayForm();
            var result = overlay.DoSnip(out Bitmap? captured, out Rectangle rect);

            if (result == OverlayResult.Aborted || captured == null)
                break;

            try
            {
                using var toolbar = new ToolbarForm(rect);
                var action = toolbar.ShowDialog();

                if (action == DialogResult.OK)
                {
                    if (ClipboardHelper.TrySetImage(captured))
                        System.Media.SystemSounds.Asterisk.Play();
                    else
                        MessageBox.Show("Panoya kopyalanamadı. Tekrar deneyin.",
                            "Kopyalama hatası", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    break;
                }
                else if (action == DialogResult.Yes)
                {
                    using var sfd = new SaveFileDialog
                    {
                        Title = "Ekran görüntüsünü kaydet",
                        Filter = "PNG (*.png)|*.png|JPEG (*.jpg;*.jpeg)|*.jpg;*.jpeg|BMP (*.bmp)|*.bmp",
                        FileName = $"snip-{DateTime.Now:yyyyMMdd-HHmmss}.png",
                        AddExtension = true,
                        OverwritePrompt = true
                    };
                    if (sfd.ShowDialog() == DialogResult.OK)
                    {
                        try
                        {
                            var ext = Path.GetExtension(sfd.FileName).ToLowerInvariant();
                            var fmt = ext switch
                            {
                                ".jpg" or ".jpeg" => ImageFormat.Jpeg,
                                ".bmp" => ImageFormat.Bmp,
                                _ => ImageFormat.Png
                            };
                            captured.Save(sfd.FileName, fmt);
                            System.Media.SystemSounds.Asterisk.Play();
                        }
                        catch (Exception ex)
                        {
                            MessageBox.Show("Kaydedilemedi:\n" + ex.Message, "Hata",
                                MessageBoxButtons.OK, MessageBoxIcon.Error);
                        }
                    }
                    break;
                }
                else if (action == DialogResult.Retry)
                {
                    continue; // yeniden seçim
                }
                else
                {
                    break; // iptal
                }
            }
            finally
            {
                captured.Dispose(); // bitmap sızıntısını engelle
            }
        }
    }
}

public enum OverlayResult { Completed, Aborted }

/* ---------- Overlay: alan seçimi ---------- */
public class OverlayForm : Form
{
    private bool _dragging;
    private Point _startVS;
    private Rectangle _selectionVS;

    public OverlayForm()
    {
        Bounds = SystemInformation.VirtualScreen;
        StartPosition = FormStartPosition.Manual;
        FormBorderStyle = FormBorderStyle.None;
        TopMost = true;
        ShowInTaskbar = false;
        DoubleBuffered = true;
        BackColor = Color.Black;
        Opacity = 0.20;
        Cursor = Cursors.Cross;
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint, true);

        KeyPreview = true;
        KeyDown += (s, e) => { if (e.KeyCode == Keys.Escape) DialogResult = DialogResult.Cancel; };

        MouseDown += OnMouseDown;
        MouseMove += OnMouseMove;
        MouseUp += OnMouseUp;
        MouseClick += (s, e) => { if (e.Button == MouseButtons.Right) DialogResult = DialogResult.Cancel; };

        Paint += OnPaint;
        Shown += (s, e) => Activate();
    }

    public OverlayResult DoSnip(out Bitmap? captured, out Rectangle selectionRect)
    {
        captured = null;
        selectionRect = Rectangle.Empty;

        var dialogRes = ShowDialog();
        if (dialogRes == DialogResult.OK)
        {
            var size = new Size(_selectionVS.Width, _selectionVS.Height);
            var bmp = new Bitmap(size.Width, size.Height, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(bmp))
                g.CopyFromScreen(_selectionVS.Location, Point.Empty, size);
            captured = bmp;
            selectionRect = _selectionVS;
            return OverlayResult.Completed;
        }
        return OverlayResult.Aborted;
    }

    private void OnMouseDown(object? s, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left) return;
        _dragging = true;
        _startVS = new Point(Bounds.Left + e.X, Bounds.Top + e.Y);
        _selectionVS = new Rectangle(_startVS, Size.Empty);
        Invalidate();
    }

    private void OnMouseMove(object? s, MouseEventArgs e)
    {
        if (!_dragging) return;
        var currentVS = new Point(Bounds.Left + e.X, Bounds.Top + e.Y);
        _selectionVS = NormalizeRect(_startVS, currentVS);
        Invalidate();
    }

    private void OnMouseUp(object? s, MouseEventArgs e)
    {
        if (!_dragging) return;
        _dragging = false;

        if (_selectionVS.Width < 2 || _selectionVS.Height < 2)
        {
            DialogResult = DialogResult.Cancel;
            return;
        }
        DialogResult = DialogResult.OK;
    }

    private void OnPaint(object? s, PaintEventArgs e)
    {
        if (_selectionVS.Width <= 0 || _selectionVS.Height <= 0) return;

        var local = VsToLocal(_selectionVS);
        using var pen = new Pen(Color.White, 2);
        e.Graphics.DrawRectangle(pen, local);
        using var brush = new SolidBrush(Color.FromArgb(50, Color.White));
        e.Graphics.FillRectangle(brush, local);
    }

    private Rectangle VsToLocal(Rectangle vs) =>
        new Rectangle(vs.Left - Bounds.Left, vs.Top - Bounds.Top, vs.Width, vs.Height);

    private static Rectangle NormalizeRect(Point a, Point b)
    {
        int x = Math.Min(a.X, b.X);
        int y = Math.Min(a.Y, b.Y);
        int w = Math.Abs(a.X - b.X);
        int h = Math.Abs(a.Y - b.Y);
        return new Rectangle(x, y, w, h);
    }
}

/* ---------- Toolbar: Kopyala / Kaydet / İptal(yeniden seç) ---------- */
public class ToolbarForm : Form
{
    private readonly Rectangle _selectionVS;

    public ToolbarForm(Rectangle selectionVS)
    {
        _selectionVS = selectionVS;

        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        TopMost = true;
        ShowInTaskbar = false;
        BackColor = SystemColors.Control;
        DoubleBuffered = true;

        var btnCopy  = MakeButton("Kopyala",  (s, e) => DialogResult = DialogResult.OK);
        var btnSave  = MakeButton("Kaydet…",  (s, e) => DialogResult = DialogResult.Yes);
        var btnRetry = MakeButton("İptal (yeniden seç)", (s, e) => DialogResult = DialogResult.Retry);

        var panel = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(10),
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            FlowDirection = FlowDirection.LeftToRight
        };
        panel.Controls.AddRange(new Control[] { btnCopy, btnSave, btnRetry });

        Controls.Add(panel);
        AutoSize = true;

        Shown += (s, e) => PositionNearSelection();
    }

    private Button MakeButton(string text, EventHandler onClick)
    {
        var b = new Button
        {
            Text = text,
            AutoSize = true,
            Margin = new Padding(6),
            UseVisualStyleBackColor = true
        };
        b.Click += onClick;
        return b;
    }

    private void PositionNearSelection()
    {
        var screen = SystemInformation.VirtualScreen;
        var desiredX = _selectionVS.Left;
        var desiredY = _selectionVS.Bottom + 8;

        Location = new Point(desiredX, desiredY);
        var rect = new Rectangle(Location, Size);

        int dx = Math.Max(0, rect.Right - (screen.Left + screen.Width));
        int dy = Math.Max(0, rect.Bottom - (screen.Top + screen.Height));
        if (dx > 0) Left -= dx + 8;
        if (dy > 0) Top = Math.Max(screen.Top, _selectionVS.Top - Height - 8);
    }

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= 0x00000080; // WS_EX_TOOLWINDOW (Alt-Tab'da görünmesin)
            return cp;
        }
    }
}