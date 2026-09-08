using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace DshLauncher
{
    // ==================== 设计系统 ====================
    // 三层令牌: primitive(色阶) → semantic(语义) → component(组件)
    // 依据 skill plugin87/ux-ui-agent-skills:
    //   tokens-and-color.md      → 3 层令牌、单一 accent、状态色必须配文字、WCAG 2.2 对比度
    //   typography-and-spacing.md→ 4px 间距基准、1.25 字号阶、字重分层
    //   components.md            → 6 状态、组件质量条、"One thing leads" 单一主行动
    static class Tokens
    {
        static Color Hex(int rgb) { return Color.FromArgb((rgb >> 16) & 0xFF, (rgb >> 8) & 0xFF, rgb & 0xFF); }

        // ---- semantic: 表面 ----
        public static readonly Color Bg           = Hex(0x0F1115);
        public static readonly Color Surface      = Hex(0x161A20);
        public static readonly Color SurfaceHover = Hex(0x1E242C);
        public static readonly Color SurfacePress = Hex(0x252C36);
        public static readonly Color Console      = Hex(0x0B0D11);

        // ---- semantic: 描边(装饰 1.2:1 / 控件 3.25:1) ----
        public static readonly Color Border       = Hex(0x262D37);
        public static readonly Color BorderStrong = Hex(0x5A6678);
        public static readonly Color Divider      = Hex(0x1F252D);

        // ---- semantic: 文字(对 surface 的实测对比度) ----
        public static readonly Color TextPrimary   = Hex(0xE8ECF1);   // 14.7:1
        public static readonly Color TextSecondary = Hex(0x9AA4B2);   //  6.9:1
        public static readonly Color TextTertiary  = Hex(0x7C8695);   //  4.7:1
        public static readonly Color TextDisabled  = Hex(0x5A6472);

        // ---- semantic: 动作(白字对比度已校验) ----
        public static readonly Color Accent      = Hex(0x1D4ED8);     // 6.7:1
        public static readonly Color AccentHover = Hex(0x2563EB);     // 5.2:1
        public static readonly Color AccentPress = Hex(0x1E40AF);     // 8.7:1
        public static readonly Color AccentText  = Hex(0x60A5FA);
        public static readonly Color OnAccent    = Hex(0xFFFFFF);

        // ---- semantic: 状态 ----
        public static readonly Color Success     = Hex(0x3FB950);
        public static readonly Color SuccessText = Hex(0x4ADE80);
        public static readonly Color Danger      = Hex(0xF2555A);
        public static readonly Color Warning     = Hex(0xD29922);

        // ---- 间距(4px 基准) ----
        public const int Sp1 = 4, Sp2 = 8, Sp3 = 12, Sp4 = 16, Sp5 = 20, Sp6 = 24, Sp8 = 32;
        // ---- 圆角 ----
        public const int RSm = 6, RMd = 8, RLg = 10;
        // ---- 字号(pt): 12 / 14 / 16 / 20 + 等宽 13 ----
        public const float FsCaption = 9f, FsBody = 10.5f, FsHead = 12f, FsTitle = 15f, FsMono = 9.5f;

        public static Color Mix(Color a, Color b, float t)
        {
            return Color.FromArgb(MixCh(a.R, b.R, t), MixCh(a.G, b.G, t), MixCh(a.B, b.B, t));
        }

        static int MixCh(int x, int y, float t)
        {
            int v = (int)Math.Round(x + (y - x) * t);
            return v < 0 ? 0 : (v > 255 ? 255 : v);
        }

        public static Color Alpha(Color c, int a) { return Color.FromArgb(a, c.R, c.G, c.B); }

        public static GraphicsPath Round(Rectangle r, int rad)
        {
            var p = new GraphicsPath();
            int d = Math.Max(2, rad * 2);
            if (d > r.Width) d = r.Width;
            if (d > r.Height) d = r.Height;
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }
    }

    // ---------- 图标: GDI+ 矢量绘制, 16 逻辑像素网格, 与文字同色 ----------
    enum IconKind { None, Play, Globe, Refresh, Search, Download, Folder, Close, Copy, External }

    static class Icons
    {
        public static void Draw(Graphics g, IconKind kind, Rectangle box, Color color)
        {
            if (kind == IconKind.None) return;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            float s = Math.Min(box.Width, box.Height);
            float cx = box.X + box.Width / 2f, cy = box.Y + box.Height / 2f;
            float stroke = Math.Max(1.2f, s / 11.5f);
            using (var pen = new Pen(color, stroke))
            {
                pen.StartCap = LineCap.Round; pen.EndCap = LineCap.Round; pen.LineJoin = LineJoin.Round;
                switch (kind)
                {
                    case IconKind.Play:
                        using (var b = new SolidBrush(color))
                        using (var p = new GraphicsPath())
                        {
                            p.AddPolygon(new[] {
                                new PointF(cx - s * 0.22f, cy - s * 0.30f),
                                new PointF(cx + s * 0.30f, cy),
                                new PointF(cx - s * 0.22f, cy + s * 0.30f) });
                            g.FillPath(b, p);
                        }
                        break;

                    case IconKind.Globe:
                        float gr = s * 0.34f;
                        g.DrawEllipse(pen, cx - gr, cy - gr, gr * 2, gr * 2);
                        g.DrawLine(pen, cx - gr, cy, cx + gr, cy);
                        g.DrawArc(pen, cx - gr * 0.5f, cy - gr, gr, gr * 2, 90, 180);
                        g.DrawArc(pen, cx - gr * 0.5f, cy - gr, gr, gr * 2, 270, 180);
                        break;

                    case IconKind.Refresh:
                        float rr = s * 0.32f;
                        g.DrawArc(pen, cx - rr, cy - rr, rr * 2, rr * 2, 40, 280);
                        double ra = 40 * Math.PI / 180.0;
                        float ax = cx + (float)(rr * Math.Cos(ra)), ay = cy + (float)(rr * Math.Sin(ra));
                        using (var b = new SolidBrush(color))
                        using (var p = new GraphicsPath())
                        {
                            p.AddPolygon(new[] {
                                new PointF(ax + s * 0.12f, ay - s * 0.13f),
                                new PointF(ax + s * 0.14f, ay + s * 0.11f),
                                new PointF(ax - s * 0.12f, ay + s * 0.02f) });
                            g.FillPath(b, p);
                        }
                        break;

                    case IconKind.Search:
                        float sr = s * 0.26f;
                        g.DrawEllipse(pen, cx - sr - s * 0.05f, cy - sr - s * 0.05f, sr * 2, sr * 2);
                        g.DrawLine(pen, cx + sr * 0.5f, cy + sr * 0.5f, cx + s * 0.34f, cy + s * 0.34f);
                        break;

                    case IconKind.Download:
                        g.DrawLine(pen, cx, cy - s * 0.34f, cx, cy + s * 0.12f);
                        using (var b = new SolidBrush(color))
                        using (var p = new GraphicsPath())
                        {
                            p.AddPolygon(new[] {
                                new PointF(cx - s * 0.16f, cy + s * 0.04f),
                                new PointF(cx + s * 0.16f, cy + s * 0.04f),
                                new PointF(cx, cy + s * 0.30f) });
                            g.FillPath(b, p);
                        }
                        g.DrawLine(pen, cx - s * 0.30f, cy + s * 0.34f, cx + s * 0.30f, cy + s * 0.34f);
                        break;

                    case IconKind.Folder:
                        float fw = s * 0.66f, fh = s * 0.44f;
                        float fl = cx - fw / 2f, ft = cy - fh / 2f + s * 0.10f;
                        g.DrawLine(pen, fl, ft, fl + fw * 0.34f, ft);
                        g.DrawLine(pen, fl + fw * 0.34f, ft, fl + fw * 0.46f, ft - s * 0.12f);
                        g.DrawLine(pen, fl + fw * 0.46f, ft - s * 0.12f, fl + fw, ft - s * 0.12f);
                        g.DrawLine(pen, fl + fw, ft - s * 0.12f, fl + fw, ft + fh);
                        g.DrawLine(pen, fl + fw, ft + fh, fl, ft + fh);
                        g.DrawLine(pen, fl, ft + fh, fl, ft);
                        break;

                    case IconKind.Close:
                        g.DrawLine(pen, cx - s * 0.27f, cy - s * 0.27f, cx + s * 0.27f, cy + s * 0.27f);
                        g.DrawLine(pen, cx + s * 0.27f, cy - s * 0.27f, cx - s * 0.27f, cy + s * 0.27f);
                        break;

                    case IconKind.Copy:
                        float cw = s * 0.40f, ch = s * 0.46f;
                        using (var p = Tokens.Round(new Rectangle(
                            (int)(cx - cw * 0.95f), (int)(cy - ch * 0.95f), (int)cw, (int)ch), Math.Max(1, (int)(s * 0.12f))))
                            g.DrawPath(pen, p);
                        using (var p = Tokens.Round(new Rectangle(
                            (int)(cx - cw * 0.05f), (int)(cy - ch * 0.05f), (int)cw, (int)ch), Math.Max(1, (int)(s * 0.12f))))
                            g.DrawPath(pen, p);
                        break;

                    case IconKind.External:
                        g.DrawLine(pen, cx - s * 0.08f, cy - s * 0.30f, cx + s * 0.30f, cy - s * 0.30f);
                        g.DrawLine(pen, cx + s * 0.30f, cy - s * 0.30f, cx + s * 0.30f, cy + s * 0.08f);
                        g.DrawLine(pen, cx + s * 0.30f, cy - s * 0.30f, cx - s * 0.04f, cy + s * 0.02f);
                        g.DrawLine(pen, cx - s * 0.30f, cy - s * 0.12f, cx - s * 0.30f, cy + s * 0.30f);
                        g.DrawLine(pen, cx - s * 0.30f, cy + s * 0.30f, cx + s * 0.12f, cy + s * 0.30f);
                        break;
                }
            }
        }
    }

    // ---------- 按钮: primary / secondary / ghost 三变体 × 6 状态 ----------
    enum BtnVariant { Primary, Secondary, Ghost }

    class UiButton : Control
    {
        BtnVariant variant;
        readonly IconKind icon;
        bool hov, dwn;

        public UiButton(string name, string text, IconKind icon, BtnVariant variant, EventHandler onClick)
        {
            Name = name;
            Text = text;
            this.icon = icon;
            this.variant = variant;
            if (onClick != null) Click += onClick;
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            BackColor = Tokens.Bg;
            Font = new Font("Microsoft YaHei UI", Tokens.FsBody, FontStyle.Regular);
            Cursor = Cursors.Hand;
            TabStop = false;
        }

        public BtnVariant Variant
        {
            get { return variant; }
            set { if (variant != value) { variant = value; Invalidate(); } }
        }

        protected override void OnMouseEnter(EventArgs e) { hov = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { hov = false; dwn = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnMouseDown(MouseEventArgs e) { if (e.Button == MouseButtons.Left) { dwn = true; Invalidate(); } base.OnMouseDown(e); }
        protected override void OnMouseUp(MouseEventArgs e) { dwn = false; Invalidate(); base.OnMouseUp(e); }
        protected override void OnEnabledChanged(EventArgs e) { hov = false; dwn = false; Invalidate(); base.OnEnabledChanged(e); }

        // 组件令牌: 变体 × 状态 → (填充, 文字, 描边)
        void Resolve(out Color fill, out Color text, out Color border)
        {
            if (!Enabled)
            {
                fill = Color.Transparent;
                text = Tokens.TextDisabled;
                border = Tokens.Divider;
                return;
            }
            switch (variant)
            {
                case BtnVariant.Primary:
                    fill = dwn ? Tokens.AccentPress : (hov ? Tokens.AccentHover : Tokens.Accent);
                    text = Tokens.OnAccent;
                    border = Color.Transparent;
                    return;
                case BtnVariant.Secondary:
                    fill = dwn ? Tokens.SurfacePress : (hov ? Tokens.SurfaceHover : Tokens.Surface);
                    text = (hov || dwn) ? Tokens.TextPrimary : Tokens.TextSecondary;
                    border = (hov || dwn) ? Tokens.TextTertiary : Tokens.BorderStrong;
                    return;
                default:
                    fill = dwn ? Tokens.SurfacePress : (hov ? Tokens.SurfaceHover : Color.Transparent);
                    text = (hov || dwn) ? Tokens.TextPrimary : Tokens.TextSecondary;
                    border = (hov || dwn) ? Tokens.Border : Color.Transparent;
                    return;
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var r = new Rectangle(0, 0, Width - 1, Height - 1);
            Color fill, text, border;
            Resolve(out fill, out text, out border);

            using (var p = Tokens.Round(r, Program.S(Tokens.RMd)))
            {
                if (fill != Color.Transparent)
                    using (var b = new SolidBrush(fill)) g.FillPath(b, p);
                if (border != Color.Transparent)
                    using (var pen = new Pen(border, Math.Max(1f, Program.UiScale))) g.DrawPath(pen, p);
            }

            // 图标 + 文字作为整体水平居中, 避免各自居中造成的错位
            int iconSize = Program.S(16);
            int gap = Program.S(Tokens.Sp2);
            Size ts = TextRenderer.MeasureText(Text, Font, new Size(int.MaxValue, int.MaxValue),
                TextFormatFlags.NoPadding | TextFormatFlags.SingleLine);
            int contentW = ts.Width + (icon == IconKind.None ? 0 : iconSize + gap);
            int x = (Width - contentW) / 2;
            if (icon != IconKind.None)
            {
                Icons.Draw(g, icon, new Rectangle(x, (Height - iconSize) / 2, iconSize, iconSize), text);
                x += iconSize + gap;
            }
            TextRenderer.DrawText(g, Text, Font, new Rectangle(x, 0, Width - x, Height), text,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix);
        }
    }

    // ---------- 状态指示器: 实心点 + 柔光; 忙碌时切换为旋转弧 ----------
    class StatusDot : Control
    {
        public Color DotColor = Tokens.Danger;
        public bool Busy;
        public float Phase;

        public StatusDot()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            BackColor = Tokens.Surface;
            Size = new Size(Program.S(20), Program.S(20));
            TabStop = false;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            float cx = Width / 2f, cy = Height / 2f;
            float rad = Program.S(4);
            if (Busy)
            {
                float rr = rad + Program.S(3);
                using (var pen = new Pen(Tokens.Alpha(DotColor, 72), Math.Max(2f, Program.S(2))))
                    g.DrawArc(pen, cx - rr, cy - rr, rr * 2, rr * 2, Phase * 360f, 250f);
                using (var b = new SolidBrush(DotColor)) g.FillEllipse(b, cx - rad, cy - rad, rad * 2, rad * 2);
                return;
            }
            float halo = rad + Program.S(3);
            using (var b = new SolidBrush(Tokens.Alpha(DotColor, 46)))
                g.FillEllipse(b, cx - halo, cy - halo, halo * 2, halo * 2);
            using (var b = new SolidBrush(DotColor))
                g.FillEllipse(b, cx - rad, cy - rad, rad * 2, rad * 2);
        }
    }
}
