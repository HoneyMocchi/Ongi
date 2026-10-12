using System;
using System.Drawing;
using System.Runtime.InteropServices;

namespace Ongi
{
    // Win32 호출 모음입니다. 작업표시줄 삽입, 레이어드 창 그리기, 네이티브 메뉴에 사용합니다.
    static class Native
    {
        [StructLayout(LayoutKind.Sequential)]
        public struct RECT
        {
            public int Left, Top, Right, Bottom;
            public int Width { get { return Right - Left; } }
            public int Height { get { return Bottom - Top; } }
            public Rectangle ToRectangle() { return Rectangle.FromLTRB(Left, Top, Right, Bottom); }
            public override string ToString() { return "(" + Left + "," + Top + " " + Width + "x" + Height + ")"; }
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct POINT { public int X, Y; }

        [StructLayout(LayoutKind.Sequential)]
        public struct SIZE { public int cx, cy; }

        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        public struct BLENDFUNCTION { public byte BlendOp, BlendFlags, SourceConstantAlpha, AlphaFormat; }

        [StructLayout(LayoutKind.Sequential)]
        public struct TRACKMOUSEEVENT { public int cbSize; public uint dwFlags; public IntPtr hwndTrack; public uint dwHoverTime; }

        public const int WS_CHILD = 0x40000000, WS_VISIBLE = 0x10000000, WS_CLIPSIBLINGS = 0x04000000;
        public const int WS_POPUP = unchecked((int)0x80000000);
        public const int WS_EX_LAYERED = 0x80000, WS_EX_TOOLWINDOW = 0x80, WS_EX_TOPMOST = 0x8, WS_EX_NOACTIVATE = 0x08000000;

        public const uint SWP_NOSIZE = 0x1, SWP_NOMOVE = 0x2, SWP_NOZORDER = 0x4, SWP_NOACTIVATE = 0x10, SWP_SHOWWINDOW = 0x40;
        public static readonly IntPtr HWND_TOP = IntPtr.Zero, HWND_TOPMOST = new IntPtr(-1);

        public const int WM_NULL = 0x0, WM_NCDESTROY = 0x82, WM_MOUSEACTIVATE = 0x21, WM_MOUSEMOVE = 0x200;
        public const int WM_LBUTTONDOWN = 0x201, WM_LBUTTONUP = 0x202, WM_RBUTTONUP = 0x205, WM_MOUSELEAVE = 0x2A3;
        public const int WM_CAPTURECHANGED = 0x215, WM_DPICHANGED = 0x2E0;
        public const int WM_APP_SHOW = 0x8001, WM_APP_EXIT = 0x8002;
        public const int MA_NOACTIVATE = 3, TME_LEAVE = 2, ULW_ALPHA = 2, GW_CHILD = 5;
        public const uint MF_STRING = 0x0, MF_CHECKED = 0x8, MF_SEPARATOR = 0x800;
        public const uint TPM_RIGHTBUTTON = 0x2, TPM_RIGHTALIGN = 0x8, TPM_BOTTOMALIGN = 0x20, TPM_RETURNCMD = 0x100;
        public const int VK_LBUTTON = 0x01, VK_RBUTTON = 0x02;

        [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr FindWindow(string cls, string title);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string cls, string title);
        [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
        [DllImport("user32.dll")] public static extern bool IsWindow(IntPtr h);
        [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
        [DllImport("user32.dll")] public static extern IntPtr GetWindow(IntPtr h, uint cmd);
        [DllImport("user32.dll")] public static extern IntPtr WindowFromPoint(POINT p);
        [DllImport("user32.dll")] public static extern uint GetDpiForWindow(IntPtr h);
        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool UpdateLayeredWindow(IntPtr h, IntPtr hdcDst, IntPtr pptDst, ref SIZE psize, IntPtr hdcSrc, ref POINT pptSrc, int crKey, ref BLENDFUNCTION blend, int flags);
        [DllImport("user32.dll")] public static extern IntPtr GetDC(IntPtr h);
        [DllImport("user32.dll")] public static extern int ReleaseDC(IntPtr h, IntPtr dc);
        [DllImport("gdi32.dll")] public static extern IntPtr CreateCompatibleDC(IntPtr dc);
        [DllImport("gdi32.dll")] public static extern bool DeleteDC(IntPtr dc);
        [DllImport("gdi32.dll")] public static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);
        [DllImport("gdi32.dll")] public static extern bool DeleteObject(IntPtr obj);
        [DllImport("user32.dll")] public static extern bool TrackMouseEvent(ref TRACKMOUSEEVENT e);
        [DllImport("user32.dll")] public static extern IntPtr SetCapture(IntPtr h);
        [DllImport("user32.dll")] public static extern bool ReleaseCapture();
        [DllImport("user32.dll")] public static extern bool GetCursorPos(out POINT p);
        [DllImport("user32.dll")] public static extern short GetAsyncKeyState(int vk);
        [DllImport("user32.dll")] public static extern IntPtr CreatePopupMenu();
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern bool AppendMenu(IntPtr m, uint flags, UIntPtr id, string text);
        [DllImport("user32.dll")] public static extern int TrackPopupMenuEx(IntPtr m, uint flags, int x, int y, IntPtr hwnd, IntPtr tpm);
        [DllImport("user32.dll")] public static extern bool DestroyMenu(IntPtr m);
        [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
        [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr h, int msg, IntPtr w, IntPtr l);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int RegisterWindowMessage(string s);
        [DllImport("dwmapi.dll")] public static extern int DwmSetWindowAttribute(IntPtr h, int attr, ref int val, int size);
        [DllImport("uxtheme.dll", EntryPoint = "#135")] static extern int SetPreferredAppMode(int mode);
        [DllImport("uxtheme.dll", EntryPoint = "#136")] static extern void FlushMenuThemes();
        [DllImport("kernel32.dll")] public static extern bool AttachConsole(int pid);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassName(IntPtr h, System.Text.StringBuilder sb, int max);

        public static string ClassOf(IntPtr h)
        {
            var sb = new System.Text.StringBuilder(256);
            GetClassName(h, sb, sb.Capacity);
            return sb.ToString();
        }

        // 주 작업표시줄과 보조 작업표시줄(다른 모니터, "모든 디스플레이에 작업 표시줄 표시"가 켜져 있을 때)을 모두 찾습니다.
        public static System.Collections.Generic.List<IntPtr> Taskbars()
        {
            var list = new System.Collections.Generic.List<IntPtr>();
            var main = FindWindow("Shell_TrayWnd", null);
            if (main != IntPtr.Zero) list.Add(main);
            for (IntPtr h = FindWindowEx(IntPtr.Zero, IntPtr.Zero, "Shell_SecondaryTrayWnd", null); h != IntPtr.Zero;
                 h = FindWindowEx(IntPtr.Zero, h, "Shell_SecondaryTrayWnd", null))
                list.Add(h);
            return list;
        }

        public static int LastLayeredError;

        // 32비트 ARGB 비트맵을 레이어드 창에 그대로 올립니다(픽셀 단위 알파).
        public static bool ApplyLayered(IntPtr hwnd, Bitmap bmp)
        {
            IntPtr screen = GetDC(IntPtr.Zero), mem = CreateCompatibleDC(screen), hbmp = IntPtr.Zero, old = IntPtr.Zero;
            try
            {
                hbmp = bmp.GetHbitmap(Color.FromArgb(0));
                old = SelectObject(mem, hbmp);
                var size = new SIZE { cx = bmp.Width, cy = bmp.Height };
                var src = new POINT();
                var blend = new BLENDFUNCTION { BlendOp = 0, BlendFlags = 0, SourceConstantAlpha = 255, AlphaFormat = 1 };
                bool ok = UpdateLayeredWindow(hwnd, screen, IntPtr.Zero, ref size, mem, ref src, 0, ref blend, ULW_ALPHA);
                LastLayeredError = ok ? 0 : Marshal.GetLastWin32Error();
                return ok;
            }
            finally
            {
                if (old != IntPtr.Zero) SelectObject(mem, old);
                if (hbmp != IntPtr.Zero) DeleteObject(hbmp);
                DeleteDC(mem);
                ReleaseDC(IntPtr.Zero, screen);
            }
        }

        // 네이티브 메뉴를 시스템 테마(다크/라이트)에 맞춥니다. 비공개 API라 실패해도 무시합니다.
        public static void ApplyMenuTheme(bool dark)
        {
            try { SetPreferredAppMode(dark ? 2 : 3); FlushMenuThemes(); } catch { }
        }

        public static float DpiScale(IntPtr hwnd)
        {
            try { uint dpi = GetDpiForWindow(hwnd); return dpi > 0 ? dpi / 96f : 1f; } catch { return 1f; }
        }
    }
}
