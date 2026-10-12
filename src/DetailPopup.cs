using System;
using System.Drawing;
using System.Windows.Forms;

namespace Ongi
{
    // 위젯을 클릭하면 뜨는 상세 패널입니다. 포커스를 빼앗지 않고, 바깥을 클릭하면 닫힙니다.
    sealed class DetailPopup : Form
    {
        readonly App app;
        readonly Timer timer = new Timer { Interval = 150 };
        float scale = 1f;
        int ticks;

        public DetailPopup(App app)
        {
            this.app = app;
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            TopMost = true;
            DoubleBuffered = true;
            AutoScaleMode = AutoScaleMode.None;
            Text = "Ongi";
            timer.Tick += OnTick;
        }

        protected override bool ShowWithoutActivation { get { return true; } }

        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                cp.ExStyle |= Native.WS_EX_TOOLWINDOW | Native.WS_EX_NOACTIVATE | Native.WS_EX_TOPMOST;
                return cp;
            }
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            try
            {
                int round = 2; // DWMWCP_ROUND: Windows 11 둥근 모서리
                Native.DwmSetWindowAttribute(Handle, 33, ref round, 4);
            }
            catch { }
        }

        public void ShowNear(Rectangle anchor, float s, bool above)
        {
            scale = s;
            var p = app.MakePalette();
            int w = (int)Math.Ceiling(PopupRenderer.Width * s), h;
            using (var bmp = new Bitmap(1, 1))
            using (var g = Graphics.FromImage(bmp))
                h = PopupRenderer.Render(g, s, w, p, app.VisibleStates, DateTime.UtcNow, false);

            var area = Screen.FromRectangle(anchor).Bounds;
            int gap = (int)(10 * s);
            int x = Math.Max(area.Left + gap, Math.Min(anchor.Right - w, area.Right - w - gap));
            int y = above ? anchor.Top - h - gap : anchor.Bottom + gap;
            if (y < area.Top + gap) y = anchor.Bottom + gap;
            if (y + h > area.Bottom - gap) y = Math.Max(area.Top + gap, anchor.Top - h - gap);

            BackColor = p.PanelBg;
            Bounds = new Rectangle(x, y, w, h);
            try
            {
                int border = ColorTranslator.ToWin32(Color.FromArgb(p.Dark ? 64 : 214, p.Dark ? 64 : 214, p.Dark ? 66 : 216));
                Native.DwmSetWindowAttribute(Handle, 34, ref border, 4);
            }
            catch { }
            if (!Visible) Show();
            Invalidate();
            ticks = 0;
            timer.Start();
        }

        // 데이터가 바뀌면 높이가 달라질 수 있어 위치를 다시 잡습니다.
        public void Reflow(Rectangle anchor, bool above)
        {
            if (Visible) ShowNear(anchor, scale, above);
        }

        public void HideNow()
        {
            timer.Stop();
            if (Visible) Hide();
            app.OnPopupHidden();
        }

        void OnTick(object sender, EventArgs e)
        {
            // 패널과 위젯 바깥을 누르면 닫습니다(라이트 디스미스). 다른 모니터의 위젯을 누르면 App이 패널을 그쪽으로 옮깁니다.
            bool pressed = (Native.GetAsyncKeyState(Native.VK_LBUTTON) & 0x8000) != 0 || (Native.GetAsyncKeyState(Native.VK_RBUTTON) & 0x8000) != 0;
            if (pressed)
            {
                var c = Cursor.Position;
                if (!Bounds.Contains(c) && !app.InAnyBar(c)) { HideNow(); return; }
            }
            if (++ticks % 33 == 0) Invalidate(); // 약 5초마다 남은 시간을 다시 그립니다.
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            HideNow();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var p = app.MakePalette();
            e.Graphics.Clear(p.PanelBg);
            PopupRenderer.Render(e.Graphics, scale, ClientSize.Width, p, app.VisibleStates, DateTime.UtcNow, true);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) timer.Dispose();
            base.Dispose(disposing);
        }
    }
}
