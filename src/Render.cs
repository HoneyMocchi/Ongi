using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Linq;

namespace Ongi
{
    static class Draw
    {
        static readonly Dictionary<string, Font> Fonts = new Dictionary<string, Font>();
        static readonly StringFormat Base = MakeFormat();

        static StringFormat MakeFormat()
        {
            var f = (StringFormat)StringFormat.GenericTypographic.Clone();
            f.FormatFlags |= StringFormatFlags.NoWrap | StringFormatFlags.NoClip | StringFormatFlags.MeasureTrailingSpaces;
            f.Trimming = StringTrimming.None;
            f.LineAlignment = StringAlignment.Center;
            return f;
        }

        public static Font Font(string family, float px)
        {
            string key = family + "|" + px.ToString("0.00");
            Font f;
            if (!Fonts.TryGetValue(key, out f)) Fonts[key] = f = new Font(family, px, GraphicsUnit.Pixel);
            return f;
        }

        // GDI+는 Segoe UI에서 한글로 대체될 때 자간이 벌어지므로 한글이 섞인 문자열은 맑은 고딕으로 그립니다.
        static Font ForText(string s, Font f)
        {
            foreach (char ch in s)
                if ((ch >= 0xAC00 && ch <= 0xD7A3) || (ch >= 0x3131 && ch <= 0x318E))
                {
                    string key = "malgun|" + f.Name + "|" + f.Size.ToString("0.00");
                    Font k;
                    if (!Fonts.TryGetValue(key, out k))
                        Fonts[key] = k = new Font("Malgun Gothic", f.Size * 0.96f, f.Name.EndsWith("Semibold") ? FontStyle.Bold : FontStyle.Regular, GraphicsUnit.Pixel);
                    return k;
                }
            return f;
        }

        public static void Setup(Graphics g, bool clearType)
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.TextRenderingHint = clearType ? TextRenderingHint.ClearTypeGridFit : TextRenderingHint.AntiAliasGridFit;
        }

        // align: -1 왼쪽, 0 가운데, 1 오른쪽. x는 정렬 기준점, cy는 세로 중심입니다.
        public static void Text(Graphics g, string s, Font f, Color c, float x, float cy, int align = -1, float maxW = 0)
        {
            f = ForText(s, f);
            using (var fmt = (StringFormat)Base.Clone())
            using (var b = new SolidBrush(c))
            {
                float w = maxW > 0 ? maxW : 2000;
                float h = f.Size * 2f;
                RectangleF r;
                if (align < 0) { fmt.Alignment = StringAlignment.Near; r = new RectangleF(x, cy - h / 2, w, h); }
                else if (align > 0) { fmt.Alignment = StringAlignment.Far; r = new RectangleF(x - w, cy - h / 2, w, h); }
                else { fmt.Alignment = StringAlignment.Center; r = new RectangleF(x - w / 2, cy - h / 2, w, h); }
                if (maxW > 0) { fmt.Trimming = StringTrimming.EllipsisCharacter; fmt.FormatFlags &= ~StringFormatFlags.NoClip; }
                g.DrawString(s, f, b, r, fmt);
            }
        }

        // 글꼴의 줄 높이가 아니라 실제 글리프 외곽을 (cx, cy)에 맞춥니다.
        // 숫자는 아래 내림 부분이 없어 줄 높이로 가운데 맞추면 위로 치우쳐 보이기 때문입니다.
        public static void Glyphs(Graphics g, string s, Font f, Color c, float cx, float cy)
        {
            using (var path = new GraphicsPath())
            using (var m = new Matrix())
            using (var b = new SolidBrush(c))
            {
                path.AddString(s, f.FontFamily, (int)f.Style, f.Size, PointF.Empty, StringFormat.GenericTypographic);
                var r = path.GetBounds();
                if (r.Width <= 0) return;
                // 가로 획이 흐려지지 않도록 위아래 이동량은 정수 픽셀로 맞춥니다.
                float dx = cx - (r.X + r.Width / 2), dy = (float)Math.Round(cy - (r.Y + r.Height / 2));
                m.Translate(dx, dy);
                path.Transform(m);
                g.FillPath(b, path);
            }
        }

        public static float Measure(Graphics g, string s, Font f)
        {
            return g.MeasureString(s, ForText(s, f), 2000, Base).Width;
        }

        public static GraphicsPath Rounded(RectangleF r, float radius)
        {
            var p = new GraphicsPath();
            float d = Math.Min(radius * 2, Math.Min(r.Width, r.Height));
            if (d <= 0.5f) { p.AddRectangle(r); return p; }
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }

        public static void FillRounded(Graphics g, RectangleF r, float radius, Color c)
        {
            using (var p = Rounded(r, radius)) using (var b = new SolidBrush(c)) g.FillPath(b, p);
        }

        public static void StrokeRounded(Graphics g, RectangleF r, float radius, Color c, float width)
        {
            using (var p = Rounded(r, radius)) using (var pen = new Pen(c, width)) g.DrawPath(pen, p);
        }

        public static void Dot(Graphics g, Color c, float cx, float cy, float radius)
        {
            using (var b = new SolidBrush(c)) g.FillEllipse(b, cx - radius, cy - radius, radius * 2, radius * 2);
        }

        // 트랙 위에 비율만큼 채운 둥근 막대입니다(상세 패널의 주간 페이스 막대에만 씁니다).
        public static void Meter(Graphics g, float x, float y, float w, float h, double pct, Color fill, Color track)
        {
            FillRounded(g, new RectangleF(x, y, w, h), h / 2, track);
            if (pct <= 0) return;
            float fw = Math.Max(h, (float)(w * Math.Min(100, pct) / 100.0));
            FillRounded(g, new RectangleF(x, y, fw, h), h / 2, fill);
        }
    }

    enum GaugeShape { Circle, Squircle }

    enum PaceState { Unknown, Room, Even, Fast, Spent }

    // 주간 페이스: 사용률을 주간 창에서 지나간 시간 비율과 비교합니다(차이 10%p 안쪽이면 적정).
    static class Pace
    {
        public const double Band = 10;

        // elapsed는 주간 창에서 지나간 비율(0~1)이며, 리셋 시각을 모르면 -1입니다.
        public static PaceState Of(UsageWindow w, DateTime now, out double used, out double elapsed)
        {
            used = w.Effective(now);
            elapsed = -1;
            if (w.ResetUtc.HasValue && w.ResetUtc.Value > now)
            {
                double len = w.LengthSec > 0 ? w.LengthSec : 7 * 86400;
                elapsed = Math.Max(0, Math.Min(1, 1 - (w.ResetUtc.Value - now).TotalSeconds / len));
            }
            if (used >= 100) return PaceState.Spent;
            if (elapsed < 0) return PaceState.Unknown;
            double diff = used - elapsed * 100;
            if (diff > Band) return PaceState.Fast;
            if (diff < -Band) return PaceState.Room;
            return PaceState.Even;
        }
    }

    // 아이폰의 데이터·Wi‑Fi·배터리 통합 아이콘처럼 아이콘 하나에 세 가지를 담습니다.
    //   링(위쪽 호)      : 5시간 사용률. 색은 서비스 색 → 75% 노랑 → 90% 빨강
    //   가운데 숫자      : 5시간 사용률(%)
    //   아래 점 5개      : 주간 사용률. 점 1개 = 20%, 채우다 만 점은 그만큼 불투명도로 표시
    //   모양             : Claude는 원, GPT는 둥근 사각형
    //   전체 불투명도    : 값이 오래되었거나 없으면 흐리게
    //   안쪽 액체        : 가장 최근 대화의 프롬프트 캐시 남은 비율(수위)과 남은 분(차분한 색). 10분 미만이면 배지로 바뀜
    //   안쪽 위 작은 점  : 캐시가 살아 있는 대화가 둘 이상일 때 대화 수(색 = 각 대화의 남은 비율)
    //   오른쪽 위 배지   : 캐시 10분 미만이면 m:ss, 한도가 다 찼으면 풀리는 시간(빨강)
    sealed class GaugeExtras
    {
        public double? Liquid;
        public Color LiquidColor;
        public List<Color> ChatDots;
        public string Badge;
        public Color BadgeFill, BadgeText;
    }

    static class Gauge
    {
        public static GaugeShape ShapeOf(string key) { return key == "claude" ? GaugeShape.Circle : GaugeShape.Squircle; }

        // 배지가 아이콘 오른쪽으로 삐져나오는 폭(논리 px, 아이콘 30 기준)
        public const float BadgeOverhang = 10f;

        public static void Render(Graphics g, GaugeShape shape, float cx, float cy, float size, double? ring, double? week,
                                  Color ringColor, Color weekColor, Color track, string number, Color numberColor, float opacity,
                                  GaugeExtras extras = null)
        {
            float k = size / 30f;
            float stroke = 3f * k;
            float r = (size - stroke) / 2f * (shape == GaugeShape.Squircle ? 0.93f : 1f);
            var loop = Loop(shape, cx, cy, r);
            var cum = Cumulative(loop);
            float len = cum[cum.Length - 1];
            float spacing = 4.4f * k, dotR = 1.5f * k;
            float gap = 4 * spacing + 2 * dotR + stroke + 2.4f * k;

            if (extras != null && extras.Liquid.HasValue && extras.Liquid.Value > 0)
                DrawLiquid(g, shape, cx, cy, r, stroke, k, extras.Liquid.Value, Palette.Fade(extras.LiquidColor, opacity));

            Color tr = Palette.Fade(track, opacity);
            using (var pen = new Pen(tr, stroke) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round })
            {
                Span(g, pen, loop, cum, gap / 2, len - gap / 2);
                if (ring.HasValue && ring.Value > 0)
                {
                    pen.Color = Palette.Fade(ringColor, opacity);
                    Span(g, pen, loop, cum, gap / 2, gap / 2 + (len - gap) * (float)Math.Min(1, ring.Value / 100));
                }
            }

            if (week.HasValue)
            {
                double v = week.Value / 20.0;
                for (int i = 0; i < 5; i++)
                {
                    // 경로는 아래 가운데에서 왼쪽으로 출발하므로 +거리가 왼쪽입니다. 왼쪽 점부터 채웁니다.
                    float d = (2 - i) * spacing;
                    var pt = PointAt(loop, cum, d < 0 ? len + d : d);
                    Draw.Dot(g, tr, pt.X, pt.Y, dotR);
                    double fill = Math.Max(0, Math.Min(1, v - i));
                    if (fill > 0) Draw.Dot(g, Palette.Fade(weekColor, opacity * fill), pt.X, pt.Y, dotR);
                }
            }

            if (extras != null && extras.ChatDots != null && extras.ChatDots.Count > 1)
            {
                int n = Math.Min(4, extras.ChatDots.Count);
                for (int i = 0; i < n; i++)
                    Draw.Dot(g, Palette.Fade(extras.ChatDots[i], opacity * 0.85), cx + (i - (n - 1) / 2f) * 3.2f * k, cy - 8.2f * k, 1.05f * k);
            }

            if (number != null)
            {
                float px = (number.Length >= 3 ? 10.5f : 13f) * k;
                Draw.Glyphs(g, number, Draw.Font("Segoe UI Semibold", px), Palette.Fade(numberColor, opacity), cx, cy);
            }

            if (extras != null && extras.Badge != null)
                DrawBadge(g, cx + 14f * k, cy - 13.5f * k, k, extras.Badge, Palette.Fade(extras.BadgeFill, opacity), extras.BadgeText);
        }

        // 링 안쪽을 잘라 액체를 채웁니다. 수면은 아주 얕은 물결입니다.
        static void DrawLiquid(Graphics g, GaugeShape shape, float cx, float cy, float r, float stroke, float k, double level, Color color)
        {
            float inset = stroke / 2 + 0.9f * k, ir = r - inset, span = 2 * ir;
            float top = cy + ir - span * (float)Math.Min(1, level);
            using (var clip = new GraphicsPath())
            using (var water = new GraphicsPath())
            using (var b = new SolidBrush(color))
            {
                if (shape == GaugeShape.Circle) clip.AddEllipse(cx - ir, cy - ir, span, span);
                else clip.AddPath(Draw.Rounded(new RectangleF(cx - ir, cy - ir, span, span), Math.Max(1, r * 0.42f - inset)), false);
                var pts = new List<PointF>();
                for (float x = cx - ir - 2; x <= cx + ir + 2; x += 1.5f * k)
                    pts.Add(new PointF(x, top + 0.55f * k * (float)Math.Sin((x - cx) / (2.6f * k))));
                pts.Add(new PointF(cx + ir + 2, cy + ir + 2));
                pts.Add(new PointF(cx - ir - 2, cy + ir + 2));
                water.AddPolygon(pts.ToArray());
                var saved = g.Clip;
                g.SetClip(clip, CombineMode.Intersect);
                g.FillPath(b, water);
                g.Clip = saved;
            }
        }

        static void DrawBadge(Graphics g, float x, float y, float k, string text, Color fill, Color textColor)
        {
            var f = Draw.Font("Segoe UI Semibold", 7.4f * k);
            float h = 9.5f * k, w = Math.Max(h, Draw.Measure(g, text, f) + 4.5f * k);
            Draw.FillRounded(g, new RectangleF(x - w / 2, y - h / 2, w, h), h / 2, fill);
            Draw.Glyphs(g, text, f, textColor, x, y);
        }

        // 아래 가운데에서 시작해 왼쪽 → 위 → 오른쪽 → 아래 가운데로 돌아오는 닫힌 경로입니다.
        static PointF[] Loop(GaugeShape shape, float cx, float cy, float r)
        {
            if (shape == GaugeShape.Circle)
            {
                const int n = 120;
                var pts = new PointF[n + 1];
                for (int i = 0; i <= n; i++)
                {
                    double a = (90 + 360.0 * i / n) * Math.PI / 180;
                    pts[i] = new PointF(cx + r * (float)Math.Cos(a), cy + r * (float)Math.Sin(a));
                }
                return pts;
            }
            float rc = r * 0.42f, d = rc * 2, l = cx - r, t = cy - r, ri = cx + r, b = cy + r;
            using (var p = new GraphicsPath())
            using (var m = new Matrix())
            {
                p.AddLine(cx, b, l + rc, b);
                p.AddArc(l, b - d, d, d, 90, 90);
                p.AddLine(l, b - rc, l, t + rc);
                p.AddArc(l, t, d, d, 180, 90);
                p.AddLine(l + rc, t, ri - rc, t);
                p.AddArc(ri - d, t, d, d, 270, 90);
                p.AddLine(ri, t + rc, ri, b - rc);
                p.AddArc(ri - d, b - d, d, d, 0, 90);
                p.AddLine(ri - rc, b, cx, b);
                p.Flatten(m, 0.05f);
                return p.PathPoints;
            }
        }

        static float[] Cumulative(PointF[] p)
        {
            var c = new float[p.Length];
            for (int i = 1; i < p.Length; i++)
            {
                float dx = p[i].X - p[i - 1].X, dy = p[i].Y - p[i - 1].Y;
                c[i] = c[i - 1] + (float)Math.Sqrt(dx * dx + dy * dy);
            }
            return c;
        }

        static PointF PointAt(PointF[] p, float[] c, float d)
        {
            for (int i = 1; i < p.Length; i++)
                if (c[i] >= d)
                {
                    float seg = c[i] - c[i - 1], t = seg > 0 ? (d - c[i - 1]) / seg : 0;
                    return new PointF(p[i - 1].X + (p[i].X - p[i - 1].X) * t, p[i - 1].Y + (p[i].Y - p[i - 1].Y) * t);
                }
            return p[p.Length - 1];
        }

        static void Span(Graphics g, Pen pen, PointF[] p, float[] c, float d0, float d1)
        {
            var list = new List<PointF> { PointAt(p, c, d0) };
            for (int i = 0; i < p.Length; i++) if (c[i] > d0 && c[i] < d1) list.Add(p[i]);
            var end = PointAt(p, c, Math.Max(d1, d0 + 0.05f));
            list.Add(end);
            g.DrawLines(pen, list.ToArray());
        }

        // 상태별 아이콘 값을 정리해 그립니다(위젯과 상세 패널이 함께 씁니다).
        // badge: 작업표시줄 위젯처럼 시간 숫자를 아이콘에 붙일지(상세 패널은 옆에 정확한 시간이 있어 뺍니다)
        public static void ForState(Graphics g, ProviderState st, float cx, float cy, float size, Palette p, DateTime now, bool badge = true)
        {
            var d = st.Data;
            var brand = p.Brand(st.Key);
            var shape = ShapeOf(st.Key);
            var extras = Extras(st, p, now, badge);
            if (d == null)
            {
                Render(g, shape, cx, cy, size, null, null, brand, brand, p.Track, st.Busy ? "…" : "—", p.Text, 0.45f, extras);
                return;
            }
            var main = d.Session ?? d.Weekly;
            double rp = main != null ? main.Effective(now) : 0;
            double? wp = d.Session != null && d.Weekly != null ? (double?)d.Weekly.Effective(now) : null;
            bool stale = st.Error != null && (now - d.UpdatedUtc).TotalMinutes > 15;
            string num = main == null ? "—" : rp >= 100 ? "100" : Math.Floor(Math.Max(0, rp)).ToString("0");
            Render(g, shape, cx, cy, size, rp, wp, p.Level(rp, brand), p.Level(wp ?? 0, brand), p.Track,
                   num, p.IsHot(rp) ? p.Level(rp, p.Text) : p.Text, stale ? 0.5f : 1f, extras);
        }

        // 한도가 다 찼으면 배지에 풀리는 시간(캐시는 의미가 없음). 아니면 가장 최근 대화의 캐시를
        // 10분 이상이면 액체로, 10분 미만이면 배지 타이머로 보여 줍니다.
        static GaugeExtras Extras(ProviderState st, Palette p, DateTime now, bool badge)
        {
            var x = new GaugeExtras { BadgeText = p.BadgeText };
            var d = st.Data;
            if (d != null)
            {
                UsageWindow full = null;
                if (d.Weekly != null && d.Weekly.Effective(now) >= 100) full = d.Weekly;
                else if (d.Session != null && d.Session.Effective(now) >= 100) full = d.Session;
                if (full != null && full.ResetUtc.HasValue && full.ResetUtc.Value > now)
                {
                    if (badge) { x.Badge = Util.Compact(full.ResetUtc.Value - now); x.BadgeFill = p.Crit; }
                    return x;
                }
            }
            var hot = ChatCache.HotList(st.Caches, now);
            if (hot.Count == 0) return x;
            double f = hot[0].Fraction(now);
            var left = hot[0].Remaining(now);
            if (left.TotalMinutes >= 10 || !badge) { x.Liquid = f; x.LiquidColor = p.Liquid(f); }
            else { x.Badge = Util.Timer(left); x.BadgeFill = p.Heat(f); }
            x.ChatDots = hot.Select(c => p.Heat(c.Fraction(now))).ToList();
            return x;
        }
    }

    // 작업표시줄 위젯: [아이콘][아이콘]  캐시·한도 시간은 아이콘 안(액체·배지)에 담습니다. (100% 배율에서 폭 약 94px)
    static class BarRenderer
    {
        const float Pad = 5, UnitGap = 4;

        // 설정의 아이콘 크기(논리 px). 48px 작업표시줄에서 38이면 배지 위쪽이 가장자리에 거의 닿으므로 그보다 키우지 않습니다.
        public static int IconLogical = 34;

        static float IconSize(float s, int height) { return Math.Max(16 * s, Math.Min(IconLogical * s, height - 10 * s)); }

        static float UnitWidth(float s, int height) { float icon = IconSize(s, height); return icon + Gauge.BadgeOverhang * icon / 30f; }

        public static int MeasureWidth(float s, int height, int rows)
        {
            // 오른쪽 끝은 패딩 없이 마지막 아이콘의 배지 자리에서 끝납니다(알림 영역에 바로 붙도록).
            return (int)Math.Ceiling(Pad * s + rows * UnitWidth(s, height) + Math.Max(0, rows - 1) * UnitGap * s);
        }

        public static void Render(Graphics g, Size size, float s, Palette p, IList<ProviderState> rows, DateTime now, Settings cfg, bool hover, bool floating)
        {
            Draw.Setup(g, false);
            // 알파 1 배경: 보이지 않지만 마우스 입력을 받습니다.
            g.Clear(Color.FromArgb(1, 0, 0, 0));
            var full = new RectangleF(0, 0, size.Width, size.Height);
            if (floating)
            {
                Draw.FillRounded(g, RectangleF.Inflate(full, -0.5f, -0.5f), 8 * s, Color.FromArgb(p.Dark ? 236 : 242, p.PanelBg));
                Draw.StrokeRounded(g, RectangleF.Inflate(full, -0.5f, -0.5f), 8 * s, p.PanelBorder, 1);
            }
            if (hover) Draw.FillRounded(g, new RectangleF(1.5f * s, 3 * s, size.Width - 3 * s, size.Height - 6 * s), 5 * s, p.Hover);

            float icon = IconSize(s, size.Height), unit = UnitWidth(s, size.Height), cy = size.Height / 2f;
            for (int i = 0; i < rows.Count; i++)
                Gauge.ForState(g, rows[i], Pad * s + i * (unit + UnitGap * s) + icon / 2, cy, icon, p, now);
        }

        // 1초마다 다시 그려야 하는지(10분 미만 캐시 타이머는 초 단위로 표시)
        public static bool NeedsSeconds(IList<ProviderState> rows, DateTime now)
        {
            foreach (var st in rows)
                foreach (var c in st.Caches)
                    if (c.Hot(now) && c.Remaining(now).TotalMinutes < 10) return true;
            return false;
        }
    }

    // 클릭하면 뜨는 상세 패널을 그립니다. draw=false면 높이만 계산합니다.
    static class PopupRenderer
    {
        public const float Width = 344;

        public static int Render(Graphics g, float s, int W, Palette basePalette, IList<ProviderState> states, DateTime now, bool draw)
        {
            Draw.Setup(g, true);
            float pad = 16 * s, y = 14 * s, col = pad + 54 * s;
            var head = Draw.Font("Segoe UI Semibold", 15f * s);
            var body = Draw.Font("Segoe UI", 13.5f * s);
            var bold = Draw.Font("Segoe UI Semibold", 13.5f * s);
            var small = Draw.Font("Segoe UI", 12.5f * s);
            var chip = Draw.Font("Segoe UI Semibold", 11f * s);

            for (int i = 0; i < states.Count; i++)
            {
                var st = states[i];
                var d = st.Data;
                var p = basePalette;
                var brand = p.Brand(st.Key);
                if (i > 0)
                {
                    if (draw) using (var pen = new Pen(p.Divider, 1)) g.DrawLine(pen, pad, y, W - pad, y);
                    y += 14 * s;
                }

                float top = y;
                if (draw) Gauge.ForState(g, st, pad + 21 * s, top + 23 * s, 42 * s, p, now, false);
                bool pace = d != null && d.Weekly != null;
                // 링과 붙어 답답하지 않도록 링 아래에 여백을 두고 막대를 놓습니다.
                if (draw && pace) PaceBar(g, s, pad, top + 64f * s, 42 * s, d.Weekly, p, brand, now);

                // 머리줄: Claude [Max 5x]                API · 방금
                float cy = top + 9 * s;
                if (draw) Draw.Text(g, st.Title, head, p.Text, col, cy);
                float cx = col + Draw.Measure(g, st.Title, head) + 7 * s;
                if (d != null && !string.IsNullOrEmpty(d.Plan))
                {
                    string plan = PlanLabel(d.Plan);
                    float pw = Draw.Measure(g, plan, chip) + 12 * s;
                    if (draw)
                    {
                        Draw.FillRounded(g, new RectangleF(cx, cy - 8.5f * s, pw, 17 * s), 8.5f * s, Color.FromArgb(p.Dark ? 52 : 34, brand));
                        Draw.Text(g, plan, chip, brand, cx + pw / 2, cy, 0);
                    }
                }
                // 갱신 시각은 평소엔 부가 정보 색, 15분 넘게 새 값을 못 받았으면 노랑으로 바꿔 오래된 값임을 알립니다.
                bool old = d != null && (now - d.UpdatedUtc).TotalMinutes > 15;
                if (draw) Draw.Text(g, Status(st, now), small, old ? Palette.Fade(p.Warn, 0.9) : p.Meta, W - pad, cy, 1);
                float ry = top + 24 * s;

                if (d == null)
                {
                    string msg = st.Error ?? (st.Busy ? "불러오는 중…" : "아직 데이터가 없습니다");
                    if (draw) Draw.Text(g, msg, small, st.Error != null ? p.Warn : p.Dim, col, ry + 10 * s, -1, W - pad - col);
                    ry += 20 * s;
                }
                else
                {
                    ry = Row(g, s, W, col, ry, "5시간", d.Session, p, body, bold, small, now, draw);
                    ry = Row(g, s, W, col, ry, "주간", d.Weekly, p, body, bold, small, now, draw);
                    foreach (var e in d.Extras)
                    {
                        if (draw)
                        {
                            string name = e.Name + "  ";
                            Draw.Text(g, name, small, p.Label, col, ry + 10 * s);
                            Draw.Text(g, Util.Pct(e.Window.Effective(now)), small, p.Dim, col + Draw.Measure(g, name, small), ry + 10 * s);
                        }
                        ry += 20 * s;
                    }
                }
                ry = CacheRows(g, s, W, col, ry, st, p, body, small, now, draw);
                string note = d != null ? (st.Error ?? d.Note) : null;
                if (note != null)
                {
                    if (draw)
                    {
                        Draw.Dot(g, p.Warn, col + 3 * s, ry + 10 * s, 3 * s);
                        Draw.Text(g, note, small, p.Warn, col + 11 * s, ry + 10 * s, -1, W - pad - col - 11 * s);
                    }
                    ry += 20 * s;
                }
                y = Math.Max(top + (pace ? 92 : 50) * s, ry) + 10 * s;
            }
            return (int)Math.Ceiling(y + 4 * s);
        }

        // 링 아래 주간 페이스 막대:
        //        ▼            ← 지금(주간 창에서 지나간 시간)
        //   ▬▬▬▬──────        ← 채움 = 주간 사용률
        //    주간 여유         ← 채움이 ▼보다 뒤면 여유, 비슷하면 적정, 앞서면 과속, 100%면 소진
        static void PaceBar(Graphics g, float s, float x, float barY, float w, UsageWindow week, Palette p, Color brand, DateTime now)
        {
            double used, elapsed;
            var state = Pace.Of(week, now, out used, out elapsed);
            float h = 5 * s;
            Color fill = state == PaceState.Spent || p.Level(used, brand) == p.Crit ? p.Crit
                       : state == PaceState.Fast || p.IsHot(used) ? p.Warn : brand;
            Draw.Meter(g, x, barY, w, h, used, fill, p.Track);

            if (elapsed >= 0)
            {
                // 지금 위치: 막대 위 작은 삼각형 + 막대를 가로지르는 얇은 눈금
                float mx = x + w * (float)elapsed;
                using (var pen = new Pen(Palette.Fade(p.Text, 0.85), Math.Max(1f, 1.2f * s)))
                    g.DrawLine(pen, mx, barY - 1.5f * s, mx, barY + h + 1.5f * s);
                using (var b = new SolidBrush(Palette.Fade(p.Text, 0.85)))
                    g.FillPolygon(b, new[] { new PointF(mx - 3f * s, barY - 5.5f * s), new PointF(mx + 3f * s, barY - 5.5f * s), new PointF(mx, barY - 1.5f * s) });
            }

            string word;
            Color wordColor;
            switch (state)
            {
                case PaceState.Room: word = "여유"; wordColor = brand; break;
                case PaceState.Fast: word = "과속"; wordColor = p.Warn; break;
                case PaceState.Spent: word = "소진"; wordColor = p.Crit; break;
                case PaceState.Even: word = "적정"; wordColor = p.Dim; break;
                default: word = "대기"; wordColor = p.Faint; break;
            }
            var f = Draw.Font("Segoe UI", 11f * s);
            var fb = Draw.Font("Segoe UI Semibold", 11f * s);
            float lw = Draw.Measure(g, "주간 ", f), ww = Draw.Measure(g, word, fb);
            float lx = x + w / 2 - (lw + ww) / 2, ly = barY + h + 12f * s;
            Draw.Text(g, "주간 ", f, p.Label, lx, ly);
            Draw.Text(g, word, fb, wordColor, lx + lw, ly);
        }

        // 5시간   42%          1시간 23분 · 오후 3:44
        static float Row(Graphics g, float s, int W, float col, float y, string label, UsageWindow w, Palette p,
                         Font body, Font bold, Font small, DateTime now, bool draw)
        {
            float pad = 16 * s, cy = y + 11 * s;
            if (draw)
            {
                Draw.Text(g, label, body, p.Label, col, cy);
                if (w == null) Draw.Text(g, "—", bold, p.Faint, col + 82 * s, cy, 1);
                else
                {
                    double pct = w.Effective(now);
                    Draw.Text(g, Util.Pct(pct), bold, p.IsHot(pct) ? p.Level(pct, p.Text) : p.Text, col + 82 * s, cy, 1);
                    // 남은 시간(판단에 쓰는 값)은 시간 색, 리셋 시각(참고)은 부가 정보 색으로 나눕니다.
                    string dur, clock;
                    ResetParts(w, now, out dur, out clock);
                    if (clock == null) Draw.Text(g, dur, small, p.Meta, W - pad, cy, 1);
                    else
                    {
                        string tail = "  " + clock;
                        Draw.Text(g, tail, small, p.Meta, W - pad, cy, 1);
                        Draw.Text(g, dur, small, p.TimeInfo, W - pad - Draw.Measure(g, tail, small), cy, 1);
                    }
                }
            }
            return y + 22 * s;
        }

        // 캐시   TTL 1시간
        //  ● 작업표시줄 위젯 만들기                52분
        //  ● 로그인 버그 추적                    4분 32초
        static float CacheRows(Graphics g, float s, int W, float col, float y, ProviderState st, Palette p,
                               Font body, Font small, DateTime now, bool draw)
        {
            const int MaxRows = 6;
            float pad = 16 * s;
            var hot = ChatCache.HotList(st.Caches, now);
            var cold = ChatCache.ColdList(st.Caches, now);
            float cy = y + 11 * s;
            var row = Draw.Font("Segoe UI", 13f * s);
            if (draw)
            {
                Draw.Text(g, "캐시", body, p.Label, col, cy);
                // 대화 목록이 아래에 바로 나오므로, 캐시가 살아 있으면 머리줄 오른쪽은 비워 둡니다.
                string right = hot.Count > 0 ? null : cold.Count > 0 ? "모두 만료" : "최근 요청 없음";
                if (right != null) Draw.Text(g, right, small, p.Meta, W - pad, cy, 1);
            }
            y += 22 * s;
            // 살아 있는 채팅(최근 순) 다음에, 최근 1시간 안에 만료된 채팅을 흐리게 보여 줍니다.
            var rows = hot.Concat(cold).ToList();
            for (int i = 0; i < rows.Count && i < MaxRows; i++)
            {
                var c = rows[i];
                bool alive = c.Hot(now);
                cy = y + 10.5f * s;
                if (draw)
                {
                    double f = c.Fraction(now);
                    // 만료된 채팅은 캐시 크기를 붙입니다. 이어 쓰면 이만큼을 다시 처리하므로, 알림 기준 이상이면 크기를 노랑으로 칠합니다.
                    string size = alive ? null : Util.Tokens(c.CachedTokens);
                    string right = alive ? Util.LongTimer(c.Remaining(now)) : "만료  " + size;
                    float rw = Draw.Measure(g, right, row);
                    // 점과 남은 시간의 색으로 캐시 남은 비율을 보여 줍니다(초록 → 모래 → 산호). 만료된 채팅은 속이 빈 점입니다.
                    Color heat = p.Heat(f);
                    if (alive) Draw.Dot(g, heat, col + 4 * s, cy, 3.5f * s);
                    else using (var pen = new Pen(p.Faint, 1.2f * s)) g.DrawEllipse(pen, col + 1.4f * s, cy - 2.6f * s, 5.2f * s, 5.2f * s);
                    if (alive) Draw.Text(g, right, row, heat, W - pad, cy, 1);
                    else
                    {
                        Color sizeColor = c.CachedTokens >= p.BigCacheTokens ? Palette.Fade(p.Warn, 0.85) : p.Meta;
                        Draw.Text(g, size, row, sizeColor, W - pad, cy, 1);
                        Draw.Text(g, "만료  ", row, p.Faint, W - pad - Draw.Measure(g, size, row), cy, 1);
                    }
                    Draw.Text(g, c.Title, row, !alive ? p.Faint : i == 0 ? p.Text : p.Dim, col + 13 * s, cy, -1, W - pad - rw - 10 * s - (col + 13 * s));
                }
                y += 21 * s;
            }
            if (rows.Count > MaxRows)
            {
                if (draw) Draw.Text(g, "외 " + (rows.Count - MaxRows) + "개", small, p.Meta, col + 13 * s, y + 9 * s);
                y += 18 * s;
            }
            return y + 2 * s;
        }

        // 리셋까지 남은 시간과 리셋 시각을 따로 돌려줍니다. 시각이 없는 상태 문구면 clock = null입니다.
        static void ResetParts(UsageWindow w, DateTime now, out string duration, out string clock)
        {
            clock = null;
            if (!w.ResetUtc.HasValue) { duration = w.Percent <= 0 ? "시작 전 · 쓰면 창이 열립니다" : "리셋 정보 없음"; return; }
            if (w.ResetUtc.Value <= now) { duration = "리셋됨 · 갱신 대기"; return; }
            duration = Util.Long(w.ResetUtc.Value - now);
            clock = Util.Clock(w.ResetUtc.Value, now);
        }

        static string Status(ProviderState st, DateTime now)
        {
            if (st.Data == null) return st.Busy ? "갱신 중…" : "";
            return (st.Busy ? "갱신 중 · " : st.Data.Source + " · ") + Util.Ago(st.Data.UpdatedUtc, now);
        }

        public static string PlanLabel(string plan)
        {
            if (string.IsNullOrEmpty(plan)) return plan;
            return char.ToUpperInvariant(plan[0]) + plan.Substring(1);
        }
    }
}
