using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Linq;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

namespace Ongi
{
    // 숨은 호스트 창입니다. 작업표시줄 재시작 알림, 두 번째 실행 신호, 메뉴 소유자 역할을 합니다.
    sealed class HostForm : Form
    {
        readonly App app;
        readonly int taskbarCreated = Native.RegisterWindowMessage("TaskbarCreated");

        public HostForm(App app, string title)
        {
            this.app = app;
            Text = title;
            ShowInTaskbar = false;
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            Location = new Point(-32000, -32000);
            Size = new Size(1, 1);
        }

        protected override void SetVisibleCore(bool value) { base.SetVisibleCore(false); }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == taskbarCreated) app.OnTaskbarCreated();
            else if (m.Msg == Native.WM_APP_SHOW) app.OnSecondInstance();
            else if (m.Msg == Native.WM_APP_EXIT) { BeginInvoke((Action)app.ExitApp); return; }
            base.WndProc(ref m);
        }
    }

    sealed class App : ApplicationContext
    {
        public readonly Options Opt;
        public readonly Settings Cfg;
        public readonly List<ProviderState> States;
        readonly HostForm host;
        readonly System.Windows.Forms.Timer timer = new System.Windows.Forms.Timer { Interval = 1000 };
        readonly DateTime started = DateTime.UtcNow;
        BarWindow bar;
        DetailPopup popup;
        DateTime recreateAt = DateTime.MaxValue, lastPopupHide;
        int tick, alertHideTick;
        bool lastDark, exiting, cacheBusy;
        NotifyIcon alertIcon;
        SettingsForm settings;
        readonly HashSet<string> alerted = new HashSet<string>();

        public App(Options opt)
        {
            Opt = opt;
            Cfg = Settings.Load(opt.Mock);
            if (opt.Floating) Cfg.Mode = "floating";
            States = new List<ProviderState>
            {
                new ProviderState("claude", "Claude", "Claude", opt.Mock ? (IProvider)new MockProvider("claude") : new ClaudeProvider()),
                new ProviderState("codex", "GPT", "GPT Codex", opt.Mock ? (IProvider)new MockProvider("codex") : new CodexProvider()),
            };
            if (!opt.Mock) Cache.Load(States);
            else foreach (var st in States) st.Caches = MockProvider.Caches(st.Key, DateTime.UtcNow);

            host = new HostForm(this, opt.Mock ? Program.HostTitle + ".Mock" : Program.HostTitle);
            host.CreateControl();
            var unused = host.Handle;

            lastDark = Palette.SystemDark();
            CreateBar();
            // 재시작 직후 캐시 값이 3분 안쪽이면 바로 다시 부르지 않습니다(재시작이 잦을 때 429 방지).
            foreach (var st in States)
                st.NextFetchUtc = st.Data != null && st.Data.Source == "API" && st.Data.UpdatedUtc.AddSeconds(180) > DateTime.UtcNow
                    ? st.Data.UpdatedUtc.AddSeconds(180) : DateTime.UtcNow;
            timer.Tick += OnTick;
            timer.Start();
            SystemEvents.PowerModeChanged += OnPowerModeChanged;
            Log.Write("시작" + (opt.Mock ? " (샘플 데이터)" : ""));
        }

        public List<ProviderState> VisibleStates
        {
            get { return States.Where(s => s.Key == "claude" ? Cfg.ShowClaude : Cfg.ShowCodex).ToList(); }
        }

        public Rectangle BarScreenRect { get { return bar != null ? bar.ScreenRect : Rectangle.Empty; } }

        public Palette MakePalette() { return new Palette(lastDark, Cfg); }

        void CreateBar()
        {
            recreateAt = DateTime.MaxValue;
            if (bar != null) { bar.Destroy(); bar = null; }
            var b = new BarWindow(this);
            bool ok = Cfg.Mode == "taskbar" && b.CreateEmbedded();
            if (!ok)
            {
                b = new BarWindow(this);
                ok = b.CreateFloating();
            }
            if (ok) bar = b;
            else recreateAt = DateTime.UtcNow.AddSeconds(5);
        }

        void OnTick(object sender, EventArgs e)
        {
            if (exiting) return;
            tick++;
            var now = DateTime.UtcNow;
            if (Opt.ExitAfter > 0 && (now - started).TotalSeconds >= Opt.ExitAfter) { ExitApp(); return; }

            if (bar == null || !bar.IsAlive)
            {
                if (bar != null && recreateAt == DateTime.MaxValue) recreateAt = now.AddSeconds(2);
                if (now >= recreateAt) CreateBar();
            }
            else if (tick % 2 == 0) bar.Reposition();
            if (tick == 3 && bar != null && bar.IsAlive) bar.LogPlacement();

            foreach (var st in VisibleStates)
                if (!st.Busy && now >= st.NextFetchUtc) StartFetch(st);

            if (!Opt.Mock && !cacheBusy && (tick == 1 || tick % 10 == 0)) StartCacheScan();
            CheckCacheAlerts(now);
            if (Opt.TestAlert && tick == 2)
                ShowAlert(new List<ChatCache>
                {
                    new ChatCache { Key = "claude", Id = "t1", Title = "결제 모듈 리팩터링", LastUtc = now.AddMinutes(-55.2), TtlSec = 3600, CachedTokens = 536000 },
                    new ChatCache { Key = "claude", Id = "t2", Title = "API 문서 초안 정리", LastUtc = now.AddMinutes(-56.5), TtlSec = 3600, CachedTokens = 1240000 },
                }, now);
            if (alertIcon != null && alertIcon.Visible && tick >= alertHideTick) alertIcon.Visible = false;

            bool dark = Palette.SystemDark();
            if (dark != lastDark || tick % 5 == 0 || BarRenderer.NeedsSeconds(VisibleStates, now))
            {
                lastDark = dark;
                // 캐시가 식어 목록이 줄면 패널 높이도 바뀌므로 5초마다 다시 배치합니다.
                Redraw(tick % 5 == 0);
            }
        }

        // 대화 기록 파일에서 채팅별 캐시 상태를 읽습니다(10초마다, 바뀐 파일만 다시 읽음).
        void StartCacheScan()
        {
            cacheBusy = true;
            int codexTtl = Cfg.CodexCacheMinutes;
            ThreadPool.QueueUserWorkItem(_ =>
            {
                List<ChatCache> claude = null, codex = null;
                var now = DateTime.UtcNow;
                try { claude = CacheScanner.Claude(now); } catch (Exception ex) { Log.Write("Claude 캐시 스캔 실패: " + ex.Message); }
                try { codex = CacheScanner.Codex(now, codexTtl); } catch (Exception ex) { Log.Write("Codex 캐시 스캔 실패: " + ex.Message); }
                try
                {
                    host.BeginInvoke((Action)(() =>
                    {
                        cacheBusy = false;
                        bool changed = false;
                        foreach (var st in States)
                        {
                            var list = st.Key == "claude" ? claude : codex;
                            if (list == null) continue;
                            if (Signature(list) != Signature(st.Caches)) changed = true;
                            bool wasActive = RecentlyActive(st, DateTime.UtcNow);
                            st.Caches = list;
                            // 쉬다가 다시 쓰기 시작했으면, 10분 주기로 미뤄 둔 조회를 활동 주기로 당깁니다.
                            if (!wasActive && RecentlyActive(st, DateTime.UtcNow) && st.RateLimitHits == 0 && !st.Busy)
                            {
                                var sooner = st.LastAttemptUtc.AddSeconds(PollInterval(st, DateTime.UtcNow));
                                if (sooner < st.NextFetchUtc) st.NextFetchUtc = sooner;
                            }
                        }
                        if (changed) Redraw(true);
                    }));
                }
                catch { cacheBusy = false; }
            });
        }

        // 큰 캐시가 식기 직전이면 Windows 알림을 한 번 띄웁니다. 그 채팅을 다시 쓰면(마지막 요청 시각이 바뀌면) 다음에 또 알립니다.
        void CheckCacheAlerts(DateTime now)
        {
            if (!Cfg.CacheAlerts) return;
            var due = new List<ChatCache>();
            foreach (var st in VisibleStates)
                foreach (var c in st.Caches)
                {
                    if (c.CachedTokens < Cfg.AlertTokens || !c.Hot(now)) continue;
                    double lead = Math.Min(Cfg.AlertMinutes * 60, c.TtlSec / 5.0);
                    string key = c.Key + c.Id + c.LastUtc.Ticks;
                    if (c.Remaining(now).TotalSeconds <= lead && alerted.Add(key)) due.Add(c);
                }
            if (due.Count > 0) ShowAlert(due, now);
        }

        void ShowAlert(List<ChatCache> due, DateTime now)
        {
            if (alertIcon == null)
            {
                alertIcon = new NotifyIcon { Text = "Ongi" };
                try { alertIcon.Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { alertIcon.Icon = SystemIcons.Information; }
                alertIcon.BalloonTipClicked += (s, e) => { alertIcon.Visible = false; if (popup == null || !popup.Visible) OnBarClick(); };
            }
            var sb = new System.Text.StringBuilder();
            foreach (var c in due.OrderBy(c => c.ExpiresUtc))
                sb.Append("• ").Append(c.Title.Length > 28 ? c.Title.Substring(0, 27) + "…" : c.Title)
                  .Append(" — ").Append(Util.LongTimer(c.Remaining(now))).Append(" 남음 · ").Append(Util.Tokens(c.CachedTokens)).Append(" 토큰\n");
            sb.Append("이어서 쓰려면 그 전에 메시지를 보내세요.");
            string text = sb.Length > 250 ? sb.ToString(0, 249) + "…" : sb.ToString();
            string title = due.Count == 1 ? "프롬프트 캐시가 곧 식습니다" : "프롬프트 캐시 " + due.Count + "개가 곧 식습니다";
            // 알림은 알림 영역 아이콘이 있어야 뜨므로, 알림 동안만 잠깐 아이콘을 켭니다.
            alertIcon.Visible = true;
            alertIcon.ShowBalloonTip(15000, title, text, ToolTipIcon.Warning);
            alertHideTick = tick + 30;
            Log.Write("캐시 알림: " + string.Join(", ", due.Select(c => c.Title + " " + Util.Tokens(c.CachedTokens))));
        }

        static string Signature(List<ChatCache> list)
        {
            var sb = new System.Text.StringBuilder();
            foreach (var c in list) sb.Append(c.Id).Append(c.LastUtc.Ticks).Append(c.Title).Append(';');
            return sb.ToString();
        }

        void StartFetch(ProviderState st)
        {
            st.Busy = true;
            st.LastAttemptUtc = DateTime.UtcNow;
            ThreadPool.QueueUserWorkItem(_ =>
            {
                Snapshot snap = null;
                Exception err = null;
                try { snap = st.Provider.Fetch(); }
                catch (Exception ex) { err = ex; }
                try { host.BeginInvoke((Action)(() => OnFetched(st, snap, err))); } catch { }
            });
        }

        void OnFetched(ProviderState st, Snapshot snap, Exception err)
        {
            st.Busy = false;
            var now = DateTime.UtcNow;
            int refresh = PollInterval(st, now);
            if (snap != null)
            {
                st.RateLimitHits = 0;
                // 로그에서 읽은 값이 캐시보다 오래되었으면 버립니다.
                if (st.Data == null || snap.UpdatedUtc >= st.Data.UpdatedUtc.AddSeconds(-1)) st.Data = snap;
                st.Error = snap.Note;
                var next = now.AddSeconds(refresh);
                var reset = st.Data.NextResetAfter(now);
                if (reset.HasValue && reset.Value.AddSeconds(5) < next) next = reset.Value.AddSeconds(5);
                st.NextFetchUtc = next;
                if (!Opt.Mock) Cache.Save(States);
            }
            else
            {
                var fe = err as FetchException;
                string msg = fe != null ? fe.Message : "오류: " + err.Message;
                if (msg != st.Error) Log.Write(st.Name + " 조회 실패: " + msg);
                st.Error = msg;
                int delay = Math.Max(refresh, fe != null ? fe.RetryAfterSec : 300);
                if (fe != null && fe.RateLimited)
                {
                    // 연속으로 429를 받으면 5 → 10 → 20 → 30분으로 늘립니다.
                    st.RateLimitHits++;
                    delay = Math.Min(1800, Math.Max(fe.RetryAfterSec, 300 << Math.Min(3, st.RateLimitHits - 1)));
                }
                st.NextFetchUtc = now.AddSeconds(delay);
            }
            Redraw(true);
        }

        // 조회 주기: 기본 refreshSeconds(90초). Claude 사용량 API는 자주 부르면 429가 나므로 최소 3분,
        // 최근 10분 동안 그 서비스로 요청한 채팅이 없으면(사용량이 거의 안 바뀜) 10분으로 늘립니다.
        int PollInterval(ProviderState st, DateTime now)
        {
            int r = Math.Max(30, Cfg.RefreshSeconds);
            if (st.Key == "claude") r = Math.Max(r, 180);
            if (!Opt.Mock && !RecentlyActive(st, now)) r = Math.Max(r, 600);
            return r;
        }

        static bool RecentlyActive(ProviderState st, DateTime now)
        {
            foreach (var c in st.Caches) if ((now - c.LastUtc).TotalMinutes < 10) return true;
            return false;
        }

        void Redraw(bool reflowPopup)
        {
            if (bar != null && bar.IsAlive) bar.Render();
            if (popup != null && popup.Visible)
            {
                if (reflowPopup && bar != null) popup.Reflow(bar.ScreenRect, PopupAbove());
                else popup.Invalidate();
            }
        }

        bool PopupAbove() { return bar == null || !bar.Embedded || bar.BottomEdge; }

        public void OnBarClick()
        {
            if (popup != null && popup.Visible) { popup.HideNow(); return; }
            if ((DateTime.UtcNow - lastPopupHide).TotalMilliseconds < 200 || bar == null) return;
            if (popup == null) popup = new DetailPopup(this);
            popup.ShowNear(bar.ScreenRect, bar.Scale, PopupAbove());
            // 패널을 열 때 오래된 값은 바로 다시 조회합니다(Claude는 429를 피하려고 2분, 요청 제한 중이면 건너뜀).
            foreach (var st in VisibleStates)
                if (!st.Busy && st.RateLimitHits == 0 && (DateTime.UtcNow - st.LastAttemptUtc).TotalSeconds > (st.Key == "claude" ? 120 : 30))
                    st.NextFetchUtc = DateTime.UtcNow;
        }

        public void HidePopup() { if (popup != null && popup.Visible) popup.HideNow(); }

        public void OnPopupHidden() { lastPopupHide = DateTime.UtcNow; }

        public void OnBarLost()
        {
            // 탐색기 재시작 등으로 부모 작업표시줄이 사라진 경우입니다.
            try { host.BeginInvoke((Action)(() => { bar = null; recreateAt = DateTime.UtcNow.AddSeconds(2); })); } catch { }
        }

        public void OnTaskbarCreated()
        {
            Log.Write("작업표시줄 재생성 감지");
            recreateAt = DateTime.UtcNow.AddSeconds(2);
            if (bar != null) { bar.Destroy(); bar = null; }
        }

        public void OnSecondInstance()
        {
            CreateBar();
            foreach (var st in VisibleStates) if (!st.Busy) st.NextFetchUtc = DateTime.UtcNow;
        }

        void OnPowerModeChanged(object sender, PowerModeChangedEventArgs e)
        {
            if (e.Mode != PowerModes.Resume) return;
            try
            {
                host.BeginInvoke((Action)(() =>
                {
                    foreach (var st in States) if (!st.Busy) st.NextFetchUtc = DateTime.UtcNow.AddSeconds(10);
                }));
            }
            catch { }
        }

        public void ShowMenu(Point screen)
        {
            HidePopup();
            Native.ApplyMenuTheme(lastDark);
            IntPtr m = Native.CreatePopupMenu();
            // 세부 항목은 설정 창으로 옮기고 메뉴는 자주 쓰는 것만 둡니다.
            Add(m, 1, "지금 새로고침");
            Sep(m);
            Add(m, 8, "설정…");
            Add(m, 4, "위치 초기화");
            Sep(m);
            Add(m, 9, "종료");

            uint flags = Native.TPM_RETURNCMD | Native.TPM_RIGHTBUTTON | Native.TPM_RIGHTALIGN;
            if (bar != null && bar.Embedded && bar.BottomEdge) flags |= Native.TPM_BOTTOMALIGN;
            Native.SetForegroundWindow(host.Handle);
            int cmd = Native.TrackPopupMenuEx(m, flags, screen.X, screen.Y, host.Handle, IntPtr.Zero);
            Native.DestroyMenu(m);
            Native.PostMessage(host.Handle, Native.WM_NULL, IntPtr.Zero, IntPtr.Zero);
            // 위젯 창의 메시지 처리 중이므로, 창을 다시 만들거나 없애는 명령은 처리가 끝난 뒤에 실행합니다.
            if (cmd > 0) host.BeginInvoke((Action)(() => RunCommand(cmd)));
        }

        void RunCommand(int cmd)
        {
            switch (cmd)
            {
                case 1:
                    foreach (var st in VisibleStates) if (!st.Busy) st.NextFetchUtc = DateTime.UtcNow;
                    break;
                case 4: Cfg.Offset = 6; Cfg.FloatX = Cfg.FloatY = null; Cfg.Save(); CreateBar(); break;
                case 8: OpenSettings(); break;
                case 9: ExitApp(); break;
            }
        }

        public void OpenSettings()
        {
            if (settings != null && !settings.IsDisposed) { settings.Activate(); return; }
            settings = new SettingsForm(Cfg, ApplySettings, bar != null ? bar.Scale : 1f);
            settings.FormClosed += (o, e) => settings = null;
            settings.Show();
            settings.Activate();
        }

        // 설정 창에서 저장하면 불립니다. 위치·표시 서비스가 바뀌었으면 위젯을 다시 만듭니다.
        public void ApplySettings(bool recreateBar)
        {
            if (recreateBar) CreateBar();
            foreach (var st in VisibleStates)
                if (!st.Busy && st.RateLimitHits == 0)
                {
                    var next = st.LastAttemptUtc.AddSeconds(PollInterval(st, DateTime.UtcNow));
                    if (next < st.NextFetchUtc) st.NextFetchUtc = next;
                }
            if (!Opt.Mock && !cacheBusy) StartCacheScan();
            Redraw(true);
        }

        static void Add(IntPtr m, int id, string text, bool check = false)
        {
            Native.AppendMenu(m, Native.MF_STRING | (check ? Native.MF_CHECKED : 0), (UIntPtr)id, text);
        }

        static void Sep(IntPtr m) { Native.AppendMenu(m, Native.MF_SEPARATOR, UIntPtr.Zero, null); }

        public void ExitApp()
        {
            if (exiting) return;
            exiting = true;
            timer.Stop();
            SystemEvents.PowerModeChanged -= OnPowerModeChanged;
            if (bar != null) bar.Destroy();
            if (popup != null) popup.Dispose();
            if (settings != null && !settings.IsDisposed) settings.Close();
            if (alertIcon != null) { alertIcon.Visible = false; alertIcon.Dispose(); }
            Log.Write("종료");
            host.Dispose();
            ExitThread();
        }
    }
}
