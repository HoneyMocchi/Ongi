using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace Ongi
{
    // 위젯 창입니다. 작업표시줄(Shell_TrayWnd)의 자식으로 넣거나, 실패하면 항상 위에 떠 있는 창으로 띄웁니다.
    sealed class BarWindow : NativeWindow
    {
        readonly App app;
        IntPtr taskbar;
        Rectangle bounds;         // 삽입 모드: 작업표시줄 클라이언트 좌표, 떠 있는 모드: 화면 좌표
        bool hover, tracking, down, dragging, closing;
        Native.POINT downPt;
        int dragOffset;
        Point dragPos;

        public bool Embedded { get; private set; }
        public bool BottomEdge { get; private set; }
        public float Scale { get; private set; }

        public BarWindow(App app) { this.app = app; Scale = 1f; BottomEdge = true; }

        public bool IsAlive { get { return Handle != IntPtr.Zero && !closing && (!Embedded || Native.IsWindow(taskbar)); } }

        public Rectangle ScreenRect
        {
            get
            {
                Native.RECT r;
                return Handle != IntPtr.Zero && Native.GetWindowRect(Handle, out r) ? r.ToRectangle() : Rectangle.Empty;
            }
        }

        public bool CreateEmbedded()
        {
            taskbar = Native.FindWindow("Shell_TrayWnd", null);
            if (taskbar == IntPtr.Zero) { Log.Write("작업표시줄을 찾지 못했습니다"); return false; }
            Rectangle r;
            if (!ComputeEmbedded(out r)) return false;

            Embedded = true;
            var cp = new CreateParams
            {
                Caption = "Ongi", X = r.X, Y = r.Y, Width = r.Width, Height = r.Height, Parent = taskbar,
                Style = Native.WS_CHILD | Native.WS_VISIBLE | Native.WS_CLIPSIBLINGS,
                ExStyle = Native.WS_EX_LAYERED | Native.WS_EX_NOACTIVATE,
            };
            try { CreateHandle(cp); }
            catch (Exception ex) { Log.Write("작업표시줄 자식 창 생성 실패: " + ex.Message); return false; }

            bounds = r;
            Native.SetWindowPos(Handle, Native.HWND_TOP, r.X, r.Y, r.Width, r.Height, Native.SWP_NOACTIVATE | Native.SWP_SHOWWINDOW);
            if (!Render())
            {
                Log.Write("UpdateLayeredWindow 실패(작업표시줄 모드): " + Native.LastLayeredError);
                Destroy();
                return false;
            }
            LogPlacement();
            return true;
        }

        public bool CreateFloating()
        {
            Embedded = false;
            taskbar = IntPtr.Zero;
            var cp = new CreateParams
            {
                Caption = "Ongi", X = -32000, Y = -32000, Width = 10, Height = 10,
                Style = Native.WS_POPUP | Native.WS_VISIBLE,
                ExStyle = Native.WS_EX_LAYERED | Native.WS_EX_TOOLWINDOW | Native.WS_EX_TOPMOST | Native.WS_EX_NOACTIVATE,
            };
            try { CreateHandle(cp); }
            catch (Exception ex) { Log.Write("떠 있는 창 생성 실패: " + ex.Message); return false; }

            PlaceFloating();
            if (!Render()) { Log.Write("UpdateLayeredWindow 실패(떠 있는 모드): " + Native.LastLayeredError); Destroy(); return false; }
            LogPlacement();
            return true;
        }

        public void LogPlacement()
        {
            var sr = ScreenRect;
            var center = new Native.POINT { X = sr.X + sr.Width / 2, Y = sr.Y + sr.Height / 2 };
            var hit = Native.WindowFromPoint(center);
            Log.Write((Embedded ? "작업표시줄 모드" : "떠 있는 모드") + " 화면좌표=" + sr + " 배율=" + Scale +
                      " 히트=" + (hit == Handle ? "Ongi" : Native.ClassOf(hit)));
        }

        // 알림 영역(TrayNotifyWnd) 바로 왼쪽에 붙입니다. Offset만큼 왼쪽으로 띄웁니다.
        bool ComputeEmbedded(out Rectangle r)
        {
            r = Rectangle.Empty;
            Native.RECT tb;
            if (!Native.GetWindowRect(taskbar, out tb) || tb.Width <= 0) return false;
            if (tb.Width < tb.Height) { Log.Write("세로 작업표시줄은 지원하지 않습니다"); return false; }

            Scale = Native.DpiScale(taskbar);
            var screen = Screen.FromHandle(taskbar).Bounds;
            BottomEdge = tb.Top > screen.Top + screen.Height / 2;

            int h = tb.Height;
            int w = BarRenderer.MeasureWidth(Scale, h, app.VisibleStates.Count);
            int trayLeft = tb.Width - (int)(320 * Scale);
            var tray = Native.FindWindowEx(taskbar, IntPtr.Zero, "TrayNotifyWnd", null);
            Native.RECT tr;
            if (tray != IntPtr.Zero && Native.GetWindowRect(tray, out tr) && tr.Width > 0) trayLeft = tr.Left - tb.Left;

            int maxOffset = Math.Max(0, (int)((trayLeft - w) / Scale));
            if (app.Cfg.Offset > maxOffset) app.Cfg.Offset = maxOffset;
            if (app.Cfg.Offset < 0) app.Cfg.Offset = 0;
            int x = trayLeft - w - (int)Math.Round(app.Cfg.Offset * Scale);
            r = new Rectangle(Math.Max(0, x), 0, w, h);
            return true;
        }

        void PlaceFloating(int depth = 0)
        {
            Scale = Native.DpiScale(Handle);
            int h = (int)Math.Ceiling(44 * Scale);
            int w = BarRenderer.MeasureWidth(Scale, h, app.VisibleStates.Count) + (int)(4 * Scale);
            var pos = app.Cfg.FloatX.HasValue && app.Cfg.FloatY.HasValue ? new Point(app.Cfg.FloatX.Value, app.Cfg.FloatY.Value) : Point.Empty;
            bool visible = false;
            foreach (var sc in Screen.AllScreens) if (sc.WorkingArea.IntersectsWith(new Rectangle(pos, new Size(w, h)))) visible = true;
            if (!app.Cfg.FloatX.HasValue || !visible)
            {
                var wa = Screen.PrimaryScreen.WorkingArea;
                pos = new Point(wa.Right - w - (int)(12 * Scale), wa.Bottom - h - (int)(12 * Scale));
            }
            bounds = new Rectangle(pos, new Size(w, h));
            Native.SetWindowPos(Handle, Native.HWND_TOPMOST, pos.X, pos.Y, w, h, Native.SWP_NOACTIVATE | Native.SWP_SHOWWINDOW);
            // 다른 DPI 모니터로 옮겨졌으면 배율을 다시 잽니다.
            float s2 = Native.DpiScale(Handle);
            if (Math.Abs(s2 - Scale) > 0.01f && depth == 0) PlaceFloating(1);
        }

        // 2초마다 불립니다. 알림 영역 폭이 바뀌거나 DPI가 바뀌면 위치·크기를 다시 맞춥니다.
        public void Reposition()
        {
            if (!IsAlive || dragging) return;
            if (Embedded)
            {
                Rectangle r;
                if (!ComputeEmbedded(out r)) return;
                if (r != bounds)
                {
                    bool resized = r.Size != bounds.Size;
                    bounds = r;
                    Native.SetWindowPos(Handle, Native.HWND_TOP, r.X, r.Y, r.Width, r.Height, Native.SWP_NOACTIVATE);
                    if (resized) Render();
                }
                if (Native.GetWindow(taskbar, Native.GW_CHILD) != Handle)
                    Native.SetWindowPos(Handle, Native.HWND_TOP, 0, 0, 0, 0, Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
            }
            else
            {
                if (Math.Abs(Native.DpiScale(Handle) - Scale) > 0.01f) { PlaceFloating(); Render(); }
                Native.SetWindowPos(Handle, Native.HWND_TOPMOST, 0, 0, 0, 0, Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
            }
        }

        public bool Render()
        {
            if (Handle == IntPtr.Zero || bounds.Width <= 0 || bounds.Height <= 0) return false;
            using (var bmp = new Bitmap(bounds.Width, bounds.Height, PixelFormat.Format32bppArgb))
            {
                using (var g = Graphics.FromImage(bmp))
                    BarRenderer.Render(g, bounds.Size, Scale, app.MakePalette(), app.VisibleStates, DateTime.UtcNow, app.Cfg, hover, !Embedded);
                return Native.ApplyLayered(Handle, bmp);
            }
        }

        public void Destroy()
        {
            closing = true;
            if (Handle != IntPtr.Zero) DestroyHandle();
        }

        protected override void WndProc(ref Message m)
        {
            switch (m.Msg)
            {
                case Native.WM_MOUSEACTIVATE:
                    m.Result = (IntPtr)Native.MA_NOACTIVATE;
                    return;
                case Native.WM_MOUSEMOVE:
                    OnMouseMove();
                    break;
                case Native.WM_MOUSELEAVE:
                    tracking = false;
                    if (hover && !down) { hover = false; Render(); }
                    break;
                case Native.WM_LBUTTONDOWN:
                    down = true;
                    dragging = false;
                    Native.GetCursorPos(out downPt);
                    dragOffset = app.Cfg.Offset;
                    dragPos = bounds.Location;
                    Native.SetCapture(Handle);
                    break;
                case Native.WM_LBUTTONUP:
                    if (down)
                    {
                        down = false;
                        bool wasDrag = dragging;
                        dragging = false;
                        Native.ReleaseCapture();
                        if (wasDrag) app.Cfg.Save();
                        else app.OnBarClick();
                    }
                    break;
                case Native.WM_CAPTURECHANGED:
                    if (down && m.LParam != Handle)
                    {
                        down = false;
                        if (dragging) { dragging = false; app.Cfg.Save(); }
                    }
                    break;
                case Native.WM_RBUTTONUP:
                    Native.POINT p;
                    Native.GetCursorPos(out p);
                    app.ShowMenu(new Point(p.X, p.Y));
                    return;
                case Native.WM_DPICHANGED:
                    if (!Embedded) { PlaceFloating(); Render(); }
                    break;
                case Native.WM_NCDESTROY:
                    if (!closing) app.OnBarLost();
                    break;
            }
            base.WndProc(ref m);
        }

        void OnMouseMove()
        {
            if (!tracking)
            {
                var t = new Native.TRACKMOUSEEVENT { cbSize = Marshal.SizeOf(typeof(Native.TRACKMOUSEEVENT)), dwFlags = Native.TME_LEAVE, hwndTrack = Handle };
                Native.TrackMouseEvent(ref t);
                tracking = true;
            }
            if (!hover) { hover = true; Render(); }
            if (!down) return;

            Native.POINT p;
            Native.GetCursorPos(out p);
            int dx = p.X - downPt.X, dy = p.Y - downPt.Y;
            if (!dragging && (Math.Abs(dx) > 4 * Scale || Math.Abs(dy) > 4 * Scale)) { dragging = true; app.HidePopup(); }
            if (!dragging) return;

            if (Embedded)
            {
                // 작업표시줄 안에서는 가로로만 옮깁니다.
                app.Cfg.Offset = Math.Max(0, dragOffset - (int)Math.Round(dx / Scale));
                Rectangle r;
                if (ComputeEmbedded(out r) && r != bounds)
                {
                    bounds = r;
                    Native.SetWindowPos(Handle, IntPtr.Zero, r.X, r.Y, 0, 0, Native.SWP_NOSIZE | Native.SWP_NOZORDER | Native.SWP_NOACTIVATE);
                }
            }
            else
            {
                bounds.Location = new Point(dragPos.X + dx, dragPos.Y + dy);
                Native.SetWindowPos(Handle, IntPtr.Zero, bounds.X, bounds.Y, 0, 0, Native.SWP_NOSIZE | Native.SWP_NOZORDER | Native.SWP_NOACTIVATE);
                app.Cfg.FloatX = bounds.X;
                app.Cfg.FloatY = bounds.Y;
            }
        }
    }
}
