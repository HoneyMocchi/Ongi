using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace Ongi
{
    // 설정 창 색입니다. 상세 패널과 같은 시스템 테마(다크/라이트)를 따릅니다.
    sealed class UiTheme
    {
        public readonly bool Dark;
        public readonly Color Bg, Field, FieldHover, Border, Text, Dim, Faint, Accent, AccentText, Selected, Track, Knob, Divider, Warn, Section;

        public UiTheme(bool dark)
        {
            Dark = dark;
            if (dark)
            {
                Bg = Color.FromArgb(32, 32, 34); Field = Color.FromArgb(45, 45, 48); FieldHover = Color.FromArgb(55, 55, 59);
                Border = Color.FromArgb(68, 68, 72); Text = Color.FromArgb(238, 238, 240); Dim = Color.FromArgb(180, 180, 186); Section = Color.FromArgb(206, 192, 172);
                Faint = Color.FromArgb(140, 140, 148); Accent = Color.FromArgb(108, 150, 212); AccentText = Color.FromArgb(16, 20, 28);
                Selected = Color.FromArgb(72, 72, 78); Track = Color.FromArgb(62, 62, 66); Knob = Color.FromArgb(196, 196, 200);
                Divider = Color.FromArgb(48, 48, 52); Warn = Color.FromArgb(226, 178, 92);
            }
            else
            {
                Bg = Color.FromArgb(249, 249, 250); Field = Color.White; FieldHover = Color.FromArgb(242, 242, 245);
                Border = Color.FromArgb(212, 212, 218); Text = Color.FromArgb(26, 26, 30); Dim = Color.FromArgb(90, 90, 98); Section = Color.FromArgb(112, 94, 72);
                Faint = Color.FromArgb(126, 126, 134); Accent = Color.FromArgb(56, 112, 190); AccentText = Color.White;
                Selected = Color.FromArgb(228, 228, 234); Track = Color.FromArgb(214, 214, 220); Knob = Color.FromArgb(92, 92, 100);
                Divider = Color.FromArgb(228, 228, 232); Warn = Color.FromArgb(176, 116, 24);
            }
        }
    }

    // 직접 그리는 컨트롤의 공통 바탕입니다.
    abstract class UiControl : Control
    {
        protected readonly UiTheme T;
        protected readonly float S;
        protected bool Hover;

        protected UiControl(UiTheme t, float s)
        {
            T = t; S = s;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
            Cursor = Cursors.Hand;
            TabStop = true;
        }

        public event EventHandler Changed;
        protected void RaiseChanged() { Invalidate(); if (Changed != null) Changed(this, EventArgs.Empty); }

        protected override void OnMouseEnter(EventArgs e) { Hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { Hover = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
        protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }

        protected Graphics Begin(PaintEventArgs e)
        {
            var g = e.Graphics;
            Draw.Setup(g, true);
            g.Clear(T.Bg);
            return g;
        }

        protected void FocusRing(Graphics g, RectangleF r, float radius)
        {
            if (Focused && ShowFocusCues) Draw.StrokeRounded(g, RectangleF.Inflate(r, 1.5f * S, 1.5f * S), radius + 1.5f * S, T.Accent, 1.5f * S);
        }
    }

    // Windows 11 스타일 켜기/끄기 스위치
    sealed class Toggle : UiControl
    {
        bool on;
        public bool Checked { get { return on; } set { if (on != value) { on = value; RaiseChanged(); } } }

        public Toggle(UiTheme t, float s, bool value) : base(t, s)
        {
            on = value;
            Size = new Size((int)(42 * s), (int)(22 * s) + 4);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = Begin(e);
            var r = new RectangleF(1.5f * S, (Height - 20 * S) / 2, 39 * S, 20 * S);
            if (on && !Enabled) Draw.FillRounded(g, r, r.Height / 2, T.Track);
            else if (on) Draw.FillRounded(g, r, r.Height / 2, T.Accent);
            else
            {
                Draw.FillRounded(g, r, r.Height / 2, Hover ? T.FieldHover : T.Field);
                Draw.StrokeRounded(g, r, r.Height / 2, T.Faint, 1.2f * S);
            }
            float kr = (on ? 6.5f : 5.5f) * S, kx = on ? r.Right - r.Height / 2 : r.X + r.Height / 2;
            Draw.Dot(g, on && Enabled ? T.AccentText : T.Knob, kx, r.Y + r.Height / 2, kr);
            FocusRing(g, r, r.Height / 2);
        }

        protected override void OnClick(EventArgs e) { Focus(); Checked = !Checked; base.OnClick(e); }
        protected override void OnKeyDown(KeyEventArgs e) { if (e.KeyCode == Keys.Space) Checked = !Checked; base.OnKeyDown(e); }
    }

    // 여러 값 중 하나를 고르는 분할 버튼: [ 작업표시줄 | 떠 있는 창 ]
    sealed class Segmented : UiControl
    {
        readonly string[] options;
        readonly float[] widths;
        int selected, hoverIndex = -1;
        readonly Font font;

        public int Selected { get { return selected; } set { if (selected != value) { selected = value; RaiseChanged(); } } }

        public Segmented(UiTheme t, float s, string[] options, int value) : base(t, s)
        {
            this.options = options;
            selected = Math.Max(0, Math.Min(options.Length - 1, value));
            font = Draw.Font("Segoe UI", 12.5f * s);
            widths = new float[options.Length];
            float total = 0;
            using (var bmp = new Bitmap(1, 1))
            using (var g = Graphics.FromImage(bmp))
                for (int i = 0; i < options.Length; i++) { widths[i] = Draw.Measure(g, options[i], font) + 24 * s; total += widths[i]; }
            Size = new Size((int)Math.Ceiling(total + 6 * s), (int)(32 * s));
        }

        int IndexAt(int x)
        {
            float cx = 3 * S;
            for (int i = 0; i < widths.Length; i++) { if (x < cx + widths[i]) return i; cx += widths[i]; }
            return widths.Length - 1;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = Begin(e);
            var r = new RectangleF(0.5f, 0.5f, Width - 1, Height - 1);
            Draw.FillRounded(g, r, 7 * S, T.Field);
            Draw.StrokeRounded(g, r, 7 * S, T.Border, 1);
            float x = 3 * S;
            for (int i = 0; i < options.Length; i++)
            {
                var seg = new RectangleF(x, 3 * S, widths[i], Height - 6 * S);
                if (i == selected) Draw.FillRounded(g, seg, 5 * S, T.Selected);
                else if (Hover && i == hoverIndex) Draw.FillRounded(g, seg, 5 * S, T.FieldHover);
                Draw.Text(g, options[i], font, !Enabled ? T.Faint : i == selected ? T.Text : T.Dim, seg.X + seg.Width / 2, seg.Y + seg.Height / 2, 0);
                x += widths[i];
            }
            FocusRing(g, r, 7 * S);
        }

        protected override void OnMouseMove(MouseEventArgs e) { int i = IndexAt(e.X); if (i != hoverIndex) { hoverIndex = i; Invalidate(); } base.OnMouseMove(e); }
        protected override void OnMouseDown(MouseEventArgs e) { Focus(); Selected = IndexAt(e.X); base.OnMouseDown(e); }

        protected override bool IsInputKey(Keys k) { return k == Keys.Left || k == Keys.Right || base.IsInputKey(k); }
        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Left) Selected = Math.Max(0, selected - 1);
            else if (e.KeyCode == Keys.Right) Selected = Math.Min(options.Length - 1, selected + 1);
            base.OnKeyDown(e);
        }
    }

    // 숫자 증감: [ −   90초   + ]  (클릭, 휠, ←→↑↓ 키)
    sealed class Stepper : UiControl
    {
        readonly int min, max, step;
        readonly Func<int, string> format;
        int value, hoverSide;
        readonly Font font;

        public int Value { get { return value; } set { int v = Math.Max(min, Math.Min(max, value)); if (v != this.value) { this.value = v; RaiseChanged(); } } }

        public Stepper(UiTheme t, float s, int min, int max, int step, int value, Func<int, string> format) : base(t, s)
        {
            this.min = min; this.max = max; this.step = step; this.format = format;
            this.value = Math.Max(min, Math.Min(max, value));
            font = Draw.Font("Segoe UI Semibold", 13f * s);
            Size = new Size((int)(140 * s), (int)(32 * s));
        }

        float Btn { get { return 32 * S; } }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = Begin(e);
            var r = new RectangleF(0.5f, 0.5f, Width - 1, Height - 1);
            Draw.FillRounded(g, r, 7 * S, T.Field);
            Draw.StrokeRounded(g, r, 7 * S, T.Border, 1);
            if (Hover && hoverSide != 0)
            {
                var hr = hoverSide < 0 ? new RectangleF(3 * S, 3 * S, Btn - 4 * S, Height - 6 * S) : new RectangleF(Width - Btn + 1 * S, 3 * S, Btn - 4 * S, Height - 6 * S);
                Draw.FillRounded(g, hr, 5 * S, T.FieldHover);
            }
            float cy = Height / 2f;
            var sign = Draw.Font("Segoe UI", 15f * S);
            Draw.Text(g, "−", sign, value > min ? T.Dim : T.Faint, Btn / 2 + 1 * S, cy, 0);
            Draw.Text(g, "+", sign, value < max ? T.Dim : T.Faint, Width - Btn / 2 - 1 * S, cy, 0);
            Draw.Text(g, format(value), font, Enabled ? T.Text : T.Faint, Width / 2f, cy, 0);
            FocusRing(g, r, 7 * S);
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            int side = e.X < Btn ? -1 : e.X > Width - Btn ? 1 : 0;
            if (side != hoverSide) { hoverSide = side; Invalidate(); }
            base.OnMouseMove(e);
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            Focus();
            if (e.X < Btn) Value -= step;
            else if (e.X > Width - Btn) Value += step;
            base.OnMouseDown(e);
        }

        protected override void OnMouseWheel(MouseEventArgs e) { Value += Math.Sign(e.Delta) * step; base.OnMouseWheel(e); }

        protected override bool IsInputKey(Keys k) { return k == Keys.Left || k == Keys.Right || k == Keys.Up || k == Keys.Down || base.IsInputKey(k); }
        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Left || e.KeyCode == Keys.Down) Value -= step;
            else if (e.KeyCode == Keys.Right || e.KeyCode == Keys.Up) Value += step;
            base.OnKeyDown(e);
        }
    }

    // 둥근 버튼(기본 = 강조색 채움, 보조 = 테두리, 텍스트 = 배경 없음)
    sealed class PillButton : UiControl
    {
        public enum Kind { Primary, Secondary, Ghost }
        readonly Kind kind;
        readonly Font font;

        public PillButton(UiTheme t, float s, string text, Kind kind) : base(t, s)
        {
            Text = text; this.kind = kind;
            font = Draw.Font(kind == Kind.Primary ? "Segoe UI Semibold" : "Segoe UI", 13f * s);
            float w;
            using (var bmp = new Bitmap(1, 1)) using (var g = Graphics.FromImage(bmp)) w = Draw.Measure(g, text, font);
            Size = new Size((int)Math.Ceiling(w + (kind == Kind.Ghost ? 12 : 36) * s), (int)(34 * s));
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = Begin(e);
            var r = new RectangleF(0.5f, 0.5f, Width - 1, Height - 1);
            Color fg = T.Text;
            if (kind == Kind.Primary)
            {
                Draw.FillRounded(g, r, 7 * S, Hover ? Color.FromArgb(230, T.Accent) : T.Accent);
                fg = T.AccentText;
            }
            else if (kind == Kind.Secondary)
            {
                Draw.FillRounded(g, r, 7 * S, Hover ? T.FieldHover : T.Field);
                Draw.StrokeRounded(g, r, 7 * S, T.Border, 1);
            }
            else fg = Hover ? T.Text : T.Dim;
            Draw.Text(g, Text, font, fg, Width / 2f, Height / 2f, 0);
            FocusRing(g, r, 7 * S);
        }

        protected override void OnKeyDown(KeyEventArgs e) { if (e.KeyCode == Keys.Space || e.KeyCode == Keys.Enter) OnClick(EventArgs.Empty); base.OnKeyDown(e); }
    }

    // 설정 창 아래 줄 오른쪽의 버전 표시입니다. 새 버전이 있으면 그 자리가 업데이트 버튼이 됩니다.
    sealed class UpdateBox : UiControl
    {
        enum State { Version, Available, Git, ReadOnly, Busy, Restarting, Failed }
        readonly IUpdateHost host;
        State state = State.Version;
        ReleaseInfo release;
        int percent;
        string error;
        readonly Font font, bold;

        public UpdateBox(UiTheme t, float s, IUpdateHost host) : base(t, s)
        {
            this.host = host;
            font = Draw.Font("Segoe UI", 12.5f * s);
            bold = Draw.Font("Segoe UI Semibold", 13f * s);
            Size = new Size((int)(250 * s), (int)(34 * s));
            Cursor = Cursors.Default;
            TabStop = false;
        }

        public void SetCheck(UpdateCheck c)
        {
            if (state == State.Busy || state == State.Restarting) return;
            release = c.Latest;
            state = !c.Newer ? State.Version
                  : c.Block == UpdateBlock.GitClone ? State.Git
                  : c.Block == UpdateBlock.ReadOnly ? State.ReadOnly : State.Available;
            Cursor = state == State.Available ? Cursors.Hand : Cursors.Default;
            TabStop = state == State.Available;
            Invalidate();
        }

        protected override void OnClick(EventArgs e)
        {
            base.OnClick(e);
            if ((state != State.Available && state != State.Failed) || release == null) return;
            state = State.Busy;
            percent = 0;
            Cursor = Cursors.Default;
            Invalidate();
            host.StartUpdate(release,
                p => { if (IsDisposed) return; percent = p; if (p >= 100) state = State.Restarting; Invalidate(); },
                msg => { if (IsDisposed) return; error = msg; state = State.Failed; Cursor = Cursors.Hand; Invalidate(); });
        }

        protected override void OnKeyDown(KeyEventArgs e) { if (e.KeyCode == Keys.Space || e.KeyCode == Keys.Enter) OnClick(EventArgs.Empty); base.OnKeyDown(e); }

        // 숫자를 읽는 소리에 맞춘 조사: 0(영)·3(삼)·6(육)은 받침이 있어 "으로", 나머지는 "로"(1 일·7 칠·8 팔은 ㄹ 받침)
        static string Ro(string version)
        {
            char last = string.IsNullOrEmpty(version) ? '0' : version[version.Length - 1];
            return last == '0' || last == '3' || last == '6' ? "으로" : "로";
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = Begin(e);
            float right = Width - 1, cy = Height / 2f;
            string next = release != null ? release.VersionText : "";
            switch (state)
            {
                case State.Version:
                    Draw.Text(g, "v" + host.CurrentVersion, font, T.Faint, right, cy, 1);
                    break;
                case State.Git:
                    Draw.Text(g, "새 버전 " + next + " · git pull로 받기", font, T.Faint, right, cy, 1);
                    break;
                case State.ReadOnly:
                    Draw.Text(g, "새 버전 " + next + " · 이 폴더에는 쓸 수 없음", font, T.Faint, right, cy, 1);
                    break;
                case State.Available:
                case State.Busy:
                case State.Restarting:
                {
                    string label = state == State.Available ? next + Ro(next) + " 업데이트"
                                 : state == State.Busy ? "받는 중 " + percent + "%" : "다시 시작하는 중…";
                    float w = Draw.Measure(g, next + Ro(next) + " 업데이트", bold) + 36 * S;
                    var r = new RectangleF(right - w, 0.5f, w - 0.5f, Height - 1);
                    if (state == State.Available)
                        Draw.FillRounded(g, r, 7 * S, Hover ? Color.FromArgb(230, T.Accent) : T.Accent);
                    else
                    {
                        // 받는 동안 버튼 자리가 진행 막대가 됩니다.
                        Draw.FillRounded(g, r, 7 * S, T.Field);
                        float fw = state == State.Restarting ? r.Width : Math.Max(14 * S, r.Width * percent / 100f);
                        Draw.FillRounded(g, new RectangleF(r.X, r.Y, fw, r.Height), 7 * S, Color.FromArgb(90, T.Accent));
                        Draw.StrokeRounded(g, r, 7 * S, T.Border, 1);
                    }
                    Draw.Text(g, label, bold, state == State.Available ? T.AccentText : T.Text, r.X + r.Width / 2, cy, 0);
                    if (state == State.Available) FocusRing(g, r, 7 * S);
                    break;
                }
                case State.Failed:
                    Draw.Text(g, "업데이트 실패 · 다시 시도", font, Hover ? T.Text : T.Warn, right, cy - 7 * S, 1);
                    Draw.Text(g, error ?? "", Draw.Font("Segoe UI", 11f * S), T.Faint, right, cy + 9 * S, 1, Width);
                    break;
            }
        }
    }

    // 설정 창: 저장 버튼 없이 항목을 바꾸는 즉시 저장·적용됩니다(디스코드 설정처럼).
    sealed class SettingsForm : Form
    {
        readonly Settings cfg;
        readonly Action<bool> apply;
        readonly UiTheme t;
        readonly float s;
        readonly List<Action<Graphics>> paints = new List<Action<Graphics>>();
        readonly Segmented mode, monitors, codexTtl, iconSize;
        readonly List<KeyValuePair<string, Toggle>> monitorToggles = new List<KeyValuePair<string, Toggle>>(); // (Screen.DeviceName, 스위치)
        static readonly string[] MonitorModes = { "primary", "all", "custom" };
        readonly Toggle showClaude, showCodex, autostart, alerts;
        readonly Stepper refresh, warn, crit, tokens, minutes;
        static readonly int[] TtlChoices = { 0, 5, 30, 60 };
        float y;

        // apply: 저장 후 호출됩니다. 인자는 위젯을 다시 만들어야 하는지(위치·모니터·표시 서비스 변경) 여부입니다.
        public SettingsForm(Settings cfg, Action<bool> apply, float scale, bool? dark = null, IUpdateHost updates = null)
        {
            this.cfg = cfg;
            this.apply = apply;
            s = scale;
            t = new UiTheme(dark ?? Palette.SystemDark());
            var c = cfg;

            Text = "Ongi 설정";
            FormBorderStyle = FormBorderStyle.FixedSingle;
            MaximizeBox = false;
            MinimizeBox = false;
            AutoScaleMode = AutoScaleMode.None;
            StartPosition = FormStartPosition.Manual;
            BackColor = t.Bg;
            ForeColor = t.Text;
            KeyPreview = true;
            DoubleBuffered = true;
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }

            int W = (int)(460 * s);
            y = 8 * s;

            Section("표시", true);
            mode = new Segmented(t, s, new[] { "작업표시줄", "떠 있는 창" }, c.Mode == "floating" ? 1 : 0);
            Row("위치", null, mode, W);
            iconSize = new Segmented(t, s, new[] { "작게", "보통", "크게" }, Math.Max(0, Array.IndexOf(Settings.IconSizes, c.IconSize)));
            Row("아이콘 크기", null, iconSize, W);
            // 작업표시줄 모드에서 위젯을 붙일 모니터입니다. 모니터가 하나뿐이면 줄을 만들지 않습니다.
            monitors = new Segmented(t, s, new[] { "주 모니터", "모든 모니터", "직접 선택" }, Math.Max(0, Array.IndexOf(MonitorModes, c.Monitors)));
            var screens = Screen.AllScreens.OrderBy(sc => sc.Bounds.X).ThenBy(sc => sc.Bounds.Y).ToArray();
            if (screens.Length > 1)
            {
                Row("모니터", "작업표시줄 위치일 때 적용", monitors, W);
                bool listed = screens.Any(sc => c.MonitorList.Contains(sc.DeviceName));
                for (int i = 0; i < screens.Length; i++)
                {
                    var sc = screens[i];
                    var tg = new Toggle(t, s, listed ? c.MonitorList.Contains(sc.DeviceName) : sc.Primary);
                    Row(MonitorName(sc), sc.Bounds.Width + "×" + sc.Bounds.Height + " · " + Side(i, screens.Length) + (sc.Primary ? " · 주 모니터" : ""), tg, W, 14);
                    monitorToggles.Add(new KeyValuePair<string, Toggle>(sc.DeviceName, tg));
                    // 하나도 안 고를 수는 없게 합니다.
                    tg.Changed += (o, e) => { if (!monitorToggles.Any(m => m.Value.Checked)) tg.Checked = true; };
                }
                Action enable = () =>
                {
                    monitors.Enabled = mode.Selected == 0;
                    foreach (var m in monitorToggles) m.Value.Enabled = mode.Selected == 0 && monitors.Selected == 2;
                };
                mode.Changed += (o, e) => enable();
                monitors.Changed += (o, e) => enable();
                enable();
            }
            showClaude = new Toggle(t, s, c.ShowClaude);
            Row("Claude", null, showClaude, W);
            showCodex = new Toggle(t, s, c.ShowCodex);
            Row("GPT (Codex)", null, showCodex, W);
            // 둘 다 끌 수는 없게 합니다.
            showClaude.Changed += (o, e) => { if (!showClaude.Checked && !showCodex.Checked) showCodex.Checked = true; };
            showCodex.Changed += (o, e) => { if (!showClaude.Checked && !showCodex.Checked) showClaude.Checked = true; };
            bool auto = false;
            try { auto = Autostart.IsEnabled(); } catch { }
            autostart = new Toggle(t, s, auto);
            Row("Windows 시작 시 실행", null, autostart, W);

            Section("사용량", false);
            refresh = new Stepper(t, s, 30, 600, 30, c.RefreshSeconds, v => v < 60 ? v + "초" : v % 60 == 0 ? (v / 60) + "분" : (v / 60) + "분 " + (v % 60) + "초");
            Row("조회 주기", "Claude는 요청 제한을 피하려고 최소 3분", refresh, W);
            warn = new Stepper(t, s, 50, 95, 5, c.WarnPercent, v => v + "%");
            Row("노랑 표시", null, warn, W);
            crit = new Stepper(t, s, 55, 100, 5, c.CritPercent, v => v + "%");
            Row("빨강 표시", null, crit, W);
            warn.Changed += (o, e) => { if (crit.Value <= warn.Value) crit.Value = warn.Value + 5; };
            crit.Changed += (o, e) => { if (crit.Value <= warn.Value) warn.Value = crit.Value - 5; };

            Section("프롬프트 캐시", false);
            alerts = new Toggle(t, s, c.CacheAlerts);
            Row("만료 알림", null, alerts, W);
            tokens = new Stepper(t, s, 50, 2000, 50, c.AlertTokens / 1000, v => v >= 1000 ? (v / 1000.0).ToString("0.##") + "M 토큰" : v + "K 토큰");
            Row("알림 기준", null, tokens, W);
            minutes = new Stepper(t, s, 1, 30, 1, c.AlertMinutes, v => v + "분 전");
            Row("알림 시점", null, minutes, W);
            alerts.Changed += (o, e) => { tokens.Enabled = minutes.Enabled = alerts.Checked; };
            tokens.Enabled = minutes.Enabled = alerts.Checked;
            int ttlIndex = Array.IndexOf(TtlChoices, c.CodexCacheMinutes);
            codexTtl = new Segmented(t, s, new[] { "자동", "5분", "30분", "1시간" }, ttlIndex < 0 ? 0 : ttlIndex);
            Row("GPT 캐시 TTL", "표시용 추정 · 자동: GPT-5.6 이후 30분", codexTtl, W);

            // 아래 줄: 로그 폴더 열기
            y += 12 * s;
            float lineY = y;
            paints.Add(g => { using (var pen = new Pen(t.Divider, 1)) g.DrawLine(pen, 0, lineY, W, lineY); });
            y += 10 * s;
            var logs = new PillButton(t, s, "로그 폴더 열기", PillButton.Kind.Ghost);
            logs.Location = new Point((int)(16 * s), (int)y);
            logs.Click += (o, e) => { try { Process.Start("explorer.exe", "\"" + Paths.Dir + "\""); } catch { } };
            Controls.Add(logs);
            if (updates != null)
            {
                var box = new UpdateBox(t, s, updates);
                box.Location = new Point(W - (int)(22 * s) - box.Width, (int)y);
                Controls.Add(box);
                updates.CheckUpdate(r => { if (!box.IsDisposed) box.SetCheck(r); });
            }
            y += logs.Height + 12 * s;

            ClientSize = new Size(W, (int)Math.Ceiling(y));
            var wa = Screen.PrimaryScreen.WorkingArea;
            Location = new Point(wa.Left + (wa.Width - Width) / 2, wa.Top + (wa.Height - Height) / 2);

            // 초기값을 다 넣은 뒤에 연결해야 창을 여는 것만으로 저장되지 않습니다.
            var live = new List<UiControl> { mode, iconSize, showClaude, showCodex, autostart, refresh, warn, crit, alerts, tokens, minutes, codexTtl };
            if (monitorToggles.Count > 0) { live.Add(monitors); live.AddRange(monitorToggles.Select(m => (UiControl)m.Value)); }
            foreach (var ctl in live) ctl.Changed += (o, e) => ApplyNow();
        }

        // \\.\DISPLAY2 → 디스플레이 2
        static string MonitorName(Screen sc)
        {
            string n = sc.DeviceName.TrimStart('\\', '.');
            return n.StartsWith("DISPLAY", StringComparison.OrdinalIgnoreCase) ? "디스플레이 " + n.Substring(7) : n;
        }

        // 왼쪽부터 놓인 순서를 말로 바꿉니다(모니터 번호가 실제 배치와 다를 때 찾기 쉽게).
        static string Side(int i, int count)
        {
            if (i == 0) return "왼쪽";
            if (i == count - 1) return "오른쪽";
            return count == 3 ? "가운데" : "왼쪽에서 " + (i + 1) + "번째";
        }

        void Section(string title, bool first)
        {
            if (!first)
            {
                y += 10 * s;
                float ly = y;
                paints.Add(g => { using (var pen = new Pen(t.Divider, 1)) g.DrawLine(pen, 22 * s, ly, ClientSize.Width - 22 * s, ly); });
                y += 10 * s;
            }
            float cy = y + 14 * s;
            var f = Draw.Font("Segoe UI Semibold", 12.5f * s);
            paints.Add(g => Draw.Text(g, title, f, t.Section, 22 * s, cy));
            y += 28 * s;
        }

        // indent: 위 항목에 딸린 하위 항목을 들여 씁니다(논리 px).
        void Row(string label, string hint, Control control, int W, float indent = 0)
        {
            float h = (hint == null ? 42 : 50) * s, cy = y + h / 2, lx = (22 + indent) * s;
            var body = Draw.Font("Segoe UI", 14f * s);
            var small = Draw.Font("Segoe UI", 12f * s);
            float ly = hint == null ? cy : cy - 9 * s;
            paints.Add(g => Draw.Text(g, label, body, control.Enabled ? t.Text : t.Faint, lx, ly));
            if (hint != null) paints.Add(g => Draw.Text(g, hint, small, t.Faint, lx, cy + 11 * s));
            control.Location = new Point(W - (int)(22 * s) - control.Width, (int)(cy - control.Height / 2f));
            control.EnabledChanged += (o, e) => { control.Invalidate(); Invalidate(); };
            Controls.Add(control);
            y += h;
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            try
            {
                int dark = t.Dark ? 1 : 0;
                Native.DwmSetWindowAttribute(Handle, 20, ref dark, 4); // 제목 표시줄도 다크로
            }
            catch { }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            Draw.Setup(g, true);
            g.Clear(t.Bg);
            foreach (var p in paints) p(g);
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Escape) Close();
            base.OnKeyDown(e);
        }

        // 항목 하나가 바뀔 때마다 전체 값을 설정에 반영하고 저장한 뒤, 위젯에 바로 적용합니다.
        void ApplyNow()
        {
            var c = cfg;
            string oldMode = c.Mode, oldMonitors = c.Monitors + "|" + string.Join(",", c.MonitorList);
            bool oldClaude = c.ShowClaude, oldCodex = c.ShowCodex;
            int oldIcon = c.IconSize;
            c.Mode = mode.Selected == 1 ? "floating" : "taskbar";
            c.IconSize = Settings.IconSizes[iconSize.Selected];
            if (monitorToggles.Count > 0)
            {
                c.Monitors = MonitorModes[monitors.Selected];
                // 목록은 직접 선택일 때만 씁니다. 지금 연결되어 있지 않은 모니터를 골라 둔 것은 그대로 둡니다.
                if (c.Monitors == "custom")
                    c.MonitorList = c.MonitorList.Where(n => !monitorToggles.Any(m => m.Key == n))
                        .Concat(monitorToggles.Where(m => m.Value.Checked).Select(m => m.Key)).ToList();
            }
            c.ShowClaude = showClaude.Checked;
            c.ShowCodex = showCodex.Checked;
            c.RefreshSeconds = refresh.Value;
            c.WarnPercent = warn.Value;
            c.CritPercent = Math.Max(crit.Value, warn.Value + 5);
            c.CacheAlerts = alerts.Checked;
            c.AlertTokens = tokens.Value * 1000;
            c.AlertMinutes = minutes.Value;
            c.CodexCacheMinutes = TtlChoices[codexTtl.Selected];
            c.Save();
            try { if (Autostart.IsEnabled() != autostart.Checked) Autostart.Set(autostart.Checked); }
            catch (Exception ex) { Log.Write("자동 실행 설정 실패: " + ex.Message); }
            apply(oldMode != c.Mode || oldIcon != c.IconSize || oldMonitors != c.Monitors + "|" + string.Join(",", c.MonitorList) || oldClaude != c.ShowClaude || oldCodex != c.ShowCodex);
        }
    }
}
