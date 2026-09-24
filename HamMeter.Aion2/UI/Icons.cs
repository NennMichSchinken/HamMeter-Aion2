using System.Globalization;
using System.Numerics;
using System.Xml.Linq;
using ImGuiNET;

namespace HamMeter.UI;

public enum Icon
{
    Settings,
    Exchange,
    ClipboardList,
    Refresh,
    Close,
    ShieldCheck,
    Zap,
    Download,
    ExternalLink,
    CircleCheck,
    CircleX,
    Folder,
    Info,
    Lock,
    LockOpen,
    Sparkles,
    Bell,
}

// Draws Lucide icons (Assets/Icons, ISC license) as ImGui strokes. Lucide icons are
// 24x24 stroke-only SVGs (stroke-width 2, round caps/joins), so each one is flattened
// once into polylines and then drawn at any size and colour, like the icon font glyphs
// the Dalamud build uses. The SVGs are embedded resources, so nothing can swap them.
internal static class Icons
{
    private static readonly Dictionary<Icon, string> Files = new()
    {
        [Icon.Settings] = "settings.svg",
        [Icon.Exchange] = "arrow-left-right.svg",
        [Icon.ClipboardList] = "clipboard-list.svg",
        [Icon.Refresh] = "refresh-cw.svg",
        [Icon.Close] = "x.svg",
        [Icon.ShieldCheck] = "shield-check.svg",
        [Icon.Zap] = "zap.svg",
        [Icon.Download] = "download.svg",
        [Icon.ExternalLink] = "external-link.svg",
        [Icon.CircleCheck] = "circle-check.svg",
        [Icon.CircleX] = "circle-x.svg",
        [Icon.Folder] = "folder.svg",
        [Icon.Info] = "info.svg",
        [Icon.Lock] = "lock.svg",
        [Icon.LockOpen] = "lock-open.svg",
        [Icon.Sparkles] = "sparkles.svg",
        [Icon.Bell] = "bell-dot.svg",
    };

    private static readonly Dictionary<Icon, LucideIcon?> Cache = new();

    public static void Draw(ImDrawListPtr dl, Icon icon, Vector2 pos, float size, uint col) =>
        Get(icon)?.Draw(dl, pos, size, col);

    internal static LucideIcon? Get(Icon icon)
    {
        if (!Cache.TryGetValue(icon, out LucideIcon? li))
        {
            using Stream? s = typeof(Icons).Assembly.GetManifestResourceStream("HamMeter.Icons." + Files[icon]);
            li = s is null ? null : LucideIcon.TryLoad(s);
            Cache[icon] = li;
        }

        return li;
    }
}

internal sealed class LucideIcon
{
    private const float ViewBox = 24f;
    private const float StrokeWidth = 2f;

    // Each stroke: flattened points, whether it closes, and its corner points (command
    // end points) where Lucide's round joins/caps get a dot.
    private readonly List<(Vector2[] Points, bool Closed, Vector2[] Corners)> m_strokes = new();

    internal int StrokeCount => m_strokes.Count;

    // Every point must stay inside Lucide's 24x24 box (catches path-parser mistakes).
    internal bool InsideViewBox => m_strokes.All(s => s.Points.All(p => p.X is >= -0.5f and <= 24.5f && p.Y is >= -0.5f and <= 24.5f));

    public static LucideIcon? TryLoad(Stream stream)
    {
        try
        {
            var icon = new LucideIcon();
            XElement svg = XElement.Load(stream);
            foreach (XElement e in svg.Descendants())
            {
                switch (e.Name.LocalName)
                {
                    case "path":
                        SvgPath.Parse((string?)e.Attribute("d") ?? string.Empty, icon.m_strokes);
                        break;
                    case "circle":
                        icon.AddEllipse(F(e, "cx"), F(e, "cy"), F(e, "r"), F(e, "r"));
                        break;
                    case "ellipse":
                        icon.AddEllipse(F(e, "cx"), F(e, "cy"), F(e, "rx"), F(e, "ry"));
                        break;
                    case "line":
                        Vector2 a = new(F(e, "x1"), F(e, "y1"));
                        Vector2 b = new(F(e, "x2"), F(e, "y2"));
                        icon.m_strokes.Add(([a, b], false, [a, b]));
                        break;
                    case "rect":
                        icon.AddRect(F(e, "x"), F(e, "y"), F(e, "width"), F(e, "height"), F(e, "rx", F(e, "ry")));
                        break;
                    case "polyline":
                    case "polygon":
                        Vector2[] pts = Points((string?)e.Attribute("points") ?? string.Empty);
                        icon.m_strokes.Add((pts, e.Name.LocalName == "polygon", pts));
                        break;
                }
            }

            return icon;
        }
        catch (Exception)
        {
            return null;
        }
    }

    public void Draw(ImDrawListPtr dl, Vector2 pos, float size, uint col)
    {
        float scale = size / ViewBox;
        float thickness = MathF.Max(1f, StrokeWidth * scale);
        float capR = thickness * 0.5f;

        foreach ((Vector2[] points, bool closed, Vector2[] corners) in m_strokes)
        {
            Vector2[] p = new Vector2[points.Length];
            for (int i = 0; i < points.Length; i++)
            {
                p[i] = pos + (points[i] * scale);
            }

            if (p.Length >= 2)
            {
                dl.AddPolyline(ref p[0], p.Length, col, closed ? ImDrawFlags.Closed : ImDrawFlags.None, thickness);
            }

            // Round caps and joins (ImGui strokes are square-ended).
            foreach (Vector2 c in corners)
            {
                dl.AddCircleFilled(pos + (c * scale), capR, col, 8);
            }
        }
    }

    private void AddEllipse(float cx, float cy, float rx, float ry)
    {
        const int n = 32;
        Vector2[] pts = new Vector2[n];
        for (int i = 0; i < n; i++)
        {
            float a = i * (2f * MathF.PI / n);
            pts[i] = new Vector2(cx + (MathF.Cos(a) * rx), cy + (MathF.Sin(a) * ry));
        }

        m_strokes.Add((pts, true, []));
    }

    private void AddRect(float x, float y, float w, float h, float r)
    {
        r = MathF.Min(r, MathF.Min(w, h) * 0.5f);
        if (r <= 0f)
        {
            Vector2[] box = [new(x, y), new(x + w, y), new(x + w, y + h), new(x, y + h)];
            m_strokes.Add((box, true, box));
            return;
        }

        List<Vector2> pts = new();
        void Corner(float cx, float cy, float start)
        {
            for (int i = 0; i <= 6; i++)
            {
                float a = start + (i * (MathF.PI / 2f) / 6f);
                pts.Add(new Vector2(cx + (MathF.Cos(a) * r), cy + (MathF.Sin(a) * r)));
            }
        }

        Corner(x + w - r, y + r, -MathF.PI / 2f);
        Corner(x + w - r, y + h - r, 0f);
        Corner(x + r, y + h - r, MathF.PI / 2f);
        Corner(x + r, y + r, MathF.PI);
        m_strokes.Add((pts.ToArray(), true, []));
    }

    private static float F(XElement e, string name, float fallback = 0f) =>
        float.TryParse((string?)e.Attribute(name), NumberStyles.Float, CultureInfo.InvariantCulture, out float v) ? v : fallback;

    private static Vector2[] Points(string s)
    {
        float[] n = s.Split([' ', ',', '\n', '\t'], StringSplitOptions.RemoveEmptyEntries)
            .Select(t => float.Parse(t, CultureInfo.InvariantCulture))
            .ToArray();
        Vector2[] pts = new Vector2[n.Length / 2];
        for (int i = 0; i < pts.Length; i++)
        {
            pts[i] = new Vector2(n[i * 2], n[(i * 2) + 1]);
        }

        return pts;
    }
}

// Minimal SVG path-data flattener: M L H V C S Q T A Z (absolute and relative).
internal static class SvgPath
{
    public static void Parse(string d, List<(Vector2[] Points, bool Closed, Vector2[] Corners)> output)
    {
        var tok = new Tokenizer(d);
        List<Vector2> pts = new();
        List<Vector2> corners = new();
        Vector2 cur = Vector2.Zero;
        Vector2 start = Vector2.Zero;
        Vector2 lastCtrl = Vector2.Zero;
        char lastCmd = ' ';
        char cmd = ' ';

        void Flush(bool closed)
        {
            if (pts.Count > 0)
            {
                // A zero-length stroke (Lucide's "h.01" dots) still needs its round cap.
                output.Add((pts.ToArray(), closed, corners.ToArray()));
            }

            pts.Clear();
            corners.Clear();
        }

        void LineTo(Vector2 p)
        {
            if (pts.Count == 0)
            {
                pts.Add(cur);
                corners.Add(cur);
            }

            pts.Add(p);
            corners.Add(p);
            cur = p;
        }

        while (tok.HasMore)
        {
            if (tok.PeekCommand(out char c))
            {
                cmd = c;
            }
            else if (cmd == 'M')
            {
                cmd = 'L'; // implicit lineto after moveto
            }
            else if (cmd == 'm')
            {
                cmd = 'l';
            }

            bool rel = char.IsLower(cmd);
            Vector2 o = rel ? cur : Vector2.Zero;

            switch (char.ToUpperInvariant(cmd))
            {
                case 'M':
                    Flush(false);
                    cur = o + tok.Point();
                    start = cur;
                    break;
                case 'L':
                    LineTo(o + tok.Point());
                    break;
                case 'H':
                    LineTo(new Vector2((rel ? cur.X : 0f) + tok.Number(), cur.Y));
                    break;
                case 'V':
                    LineTo(new Vector2(cur.X, (rel ? cur.Y : 0f) + tok.Number()));
                    break;
                case 'C':
                {
                    Vector2 c1 = o + tok.Point();
                    Vector2 c2 = o + tok.Point();
                    Vector2 e = o + tok.Point();
                    Cubic(cur, c1, c2, e);
                    lastCtrl = c2;
                    break;
                }

                case 'S':
                {
                    Vector2 c1 = char.ToUpperInvariant(lastCmd) is 'C' or 'S' ? (2 * cur) - lastCtrl : cur;
                    Vector2 c2 = o + tok.Point();
                    Vector2 e = o + tok.Point();
                    Cubic(cur, c1, c2, e);
                    lastCtrl = c2;
                    break;
                }

                case 'Q':
                {
                    Vector2 q = o + tok.Point();
                    Vector2 e = o + tok.Point();
                    Cubic(cur, cur + ((q - cur) * (2f / 3f)), e + ((q - e) * (2f / 3f)), e);
                    lastCtrl = q;
                    break;
                }

                case 'T':
                {
                    Vector2 q = char.ToUpperInvariant(lastCmd) is 'Q' or 'T' ? (2 * cur) - lastCtrl : cur;
                    Vector2 e = o + tok.Point();
                    Cubic(cur, cur + ((q - cur) * (2f / 3f)), e + ((q - e) * (2f / 3f)), e);
                    lastCtrl = q;
                    break;
                }

                case 'A':
                {
                    float rx = tok.Number();
                    float ry = tok.Number();
                    float rot = tok.Number();
                    bool large = tok.Flag();
                    bool sweep = tok.Flag();
                    Vector2 e = o + tok.Point();
                    Arc(cur, e, rx, ry, rot, large, sweep);
                    break;
                }

                case 'Z':
                    if (pts.Count > 0 && cur != start)
                    {
                        pts.Add(start);
                    }

                    cur = start;
                    Flush(true);
                    break;

                default:
                    return; // unknown command: stop rather than draw garbage
            }

            lastCmd = cmd;
        }

        Flush(false);

        void Cubic(Vector2 p0, Vector2 p1, Vector2 p2, Vector2 p3)
        {
            const int n = 12;
            if (pts.Count == 0)
            {
                pts.Add(p0);
                corners.Add(p0);
            }

            for (int i = 1; i <= n; i++)
            {
                float t = i / (float)n;
                float u = 1f - t;
                pts.Add((u * u * u * p0) + (3 * u * u * t * p1) + (3 * u * t * t * p2) + (t * t * t * p3));
            }

            corners.Add(p3);
            cur = p3;
        }

        // Endpoint -> center parameterisation (SVG spec, appendix F.6.5).
        void Arc(Vector2 p0, Vector2 p1, float rx, float ry, float rotDeg, bool large, bool sweep)
        {
            if (rx == 0 || ry == 0 || p0 == p1)
            {
                LineTo(p1);
                return;
            }

            rx = MathF.Abs(rx);
            ry = MathF.Abs(ry);
            float phi = rotDeg * MathF.PI / 180f;
            float cos = MathF.Cos(phi);
            float sin = MathF.Sin(phi);

            Vector2 d = (p0 - p1) * 0.5f;
            float x1 = (cos * d.X) + (sin * d.Y);
            float y1 = (-sin * d.X) + (cos * d.Y);

            float lambda = ((x1 * x1) / (rx * rx)) + ((y1 * y1) / (ry * ry));
            if (lambda > 1f)
            {
                float s = MathF.Sqrt(lambda);
                rx *= s;
                ry *= s;
            }

            float num = (rx * rx * ry * ry) - (rx * rx * y1 * y1) - (ry * ry * x1 * x1);
            float den = (rx * rx * y1 * y1) + (ry * ry * x1 * x1);
            float coef = MathF.Sqrt(MathF.Max(0f, num / den)) * (large == sweep ? -1f : 1f);
            float cx1 = coef * (rx * y1 / ry);
            float cy1 = coef * -(ry * x1 / rx);

            Vector2 mid = (p0 + p1) * 0.5f;
            Vector2 center = new(mid.X + (cos * cx1) - (sin * cy1), mid.Y + (sin * cx1) + (cos * cy1));

            float Angle(Vector2 u, Vector2 v)
            {
                float a = MathF.Atan2((u.X * v.Y) - (u.Y * v.X), Vector2.Dot(u, v));
                return a;
            }

            Vector2 v1 = new((x1 - cx1) / rx, (y1 - cy1) / ry);
            Vector2 v2 = new((-x1 - cx1) / rx, (-y1 - cy1) / ry);
            float theta = Angle(Vector2.UnitX, v1);
            float delta = Angle(v1, v2);
            if (!sweep && delta > 0)
            {
                delta -= 2f * MathF.PI;
            }
            else if (sweep && delta < 0)
            {
                delta += 2f * MathF.PI;
            }

            if (pts.Count == 0)
            {
                pts.Add(p0);
                corners.Add(p0);
            }

            int n = Math.Max(4, (int)MathF.Ceiling(MathF.Abs(delta) / (MathF.PI / 12f)));
            for (int i = 1; i <= n; i++)
            {
                float a = theta + (delta * i / n);
                float ex = rx * MathF.Cos(a);
                float ey = ry * MathF.Sin(a);
                pts.Add(new Vector2(center.X + (cos * ex) - (sin * ey), center.Y + (sin * ex) + (cos * ey)));
            }

            pts[^1] = p1;
            corners.Add(p1);
            cur = p1;
        }
    }

    private sealed class Tokenizer(string s)
    {
        private int m_pos;

        public bool HasMore
        {
            get
            {
                this.SkipSeparators();
                return m_pos < s.Length;
            }
        }

        public bool PeekCommand(out char c)
        {
            this.SkipSeparators();
            c = m_pos < s.Length ? s[m_pos] : ' ';
            if (char.IsLetter(c) && c is not ('e' or 'E'))
            {
                m_pos++;
                return true;
            }

            return false;
        }

        public Vector2 Point() => new(this.Number(), this.Number());

        // Arc flags may be packed without separators ("a1 1 0 011 1").
        public bool Flag()
        {
            this.SkipSeparators();
            return s[m_pos++] == '1';
        }

        public float Number()
        {
            this.SkipSeparators();
            int begin = m_pos;
            if (m_pos < s.Length && s[m_pos] is '-' or '+')
            {
                m_pos++;
            }

            bool dot = false;
            while (m_pos < s.Length)
            {
                char c = s[m_pos];
                if (char.IsDigit(c))
                {
                    m_pos++;
                }
                else if (c == '.' && !dot)
                {
                    dot = true;
                    m_pos++;
                }
                else if (c is 'e' or 'E')
                {
                    m_pos++;
                    if (m_pos < s.Length && s[m_pos] is '-' or '+')
                    {
                        m_pos++;
                    }
                }
                else
                {
                    break;
                }
            }

            return float.Parse(s.AsSpan(begin, m_pos - begin), NumberStyles.Float, CultureInfo.InvariantCulture);
        }

        private void SkipSeparators()
        {
            while (m_pos < s.Length && (char.IsWhiteSpace(s[m_pos]) || s[m_pos] == ','))
            {
                m_pos++;
            }
        }
    }
}
