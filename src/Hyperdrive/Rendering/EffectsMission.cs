using Hyperdrive.Core;
using UnityEngine;

namespace Hyperdrive.Rendering
{
    /// <summary>A rocket drawn as a blueprint in the VAB: the pen follows the outline as the game loads.</summary>
    internal sealed class BlueprintEffect : LoadingEffect
    {
        // Half profile of a two-stage rocket with its escape tower, bottom to top, in rocket heights (x is a radius).
        private static readonly float[] Profile =
        {
            0.085f, 0f, 0.055f, 0.05f, 0.07f, 0.065f, 0.09f, 0.075f, 0.105f, 0.09f, 0.105f, 0.42f, 0.09f, 0.44f, 0.09f, 0.64f,
            0.075f, 0.66f, 0.075f, 0.70f, 0.068f, 0.705f, 0.03f, 0.83f, 0.012f, 0.84f, 0.012f, 0.96f, 0f, 1f
        };
        private static readonly float[] Fin = { 0.105f, 0.09f, 0.19f, 0.02f, 0.19f, 0.08f, 0.105f, 0.21f };
        private static readonly float[] Stations = { 0.075f, 0.42f, 0.64f, 0.70f, 0.84f };

        private float[] _seg;   // x0, y0, x1, y1 per segment, in drawing order
        private int _count;
        private float _total;

        public override string Name => "blueprint";
        public override Color Background => new Color(0.032f, 0.078f, 0.14f, 1f);

        public override void Init(EffectContext c, System.Random rng)
        {
            int n = (Profile.Length / 2 - 1) * 2 + (Fin.Length / 2 - 1) * 2;
            _seg = new float[n * 4];
            _count = 0;
            for (int side = 1; side >= -1; side -= 2)
                AddPolyline(Profile, side);
            for (int side = 1; side >= -1; side -= 2)
                AddPolyline(Fin, side);
            _total = 0f;
            for (int i = 0; i < _count; i++)
                _total += Length(i);
        }

        private void AddPolyline(float[] pts, int side)
        {
            for (int i = 0; i + 3 < pts.Length; i += 2)
            {
                int k = _count++ * 4;
                _seg[k] = pts[i] * side; _seg[k + 1] = pts[i + 1]; _seg[k + 2] = pts[i + 2] * side; _seg[k + 3] = pts[i + 3];
            }
        }

        private float Length(int i)
        {
            int k = i * 4;
            float dx = _seg[k + 2] - _seg[k], dy = _seg[k + 3] - _seg[k + 1];
            return Mathf.Sqrt(dx * dx + dy * dy);
        }

        public override void Draw(EffectContext c)
        {
            float w = c.W, h = c.H, s = h * 0.70f, ox = w * 0.40f, oy = h * 0.14f;
            if (!c.Blend())
                return;
            GL.Begin(GL.LINES);
            float step = h / 40f;
            Color minor = c.Blended(new Color(0.55f, 0.78f, 1f), 0.055f), major = c.Blended(new Color(0.55f, 0.78f, 1f), 0.12f);
            int first = -Mathf.CeilToInt(ox / step);
            for (int i = first; ox + i * step < w; i++)
                Gfx.Line(ox + i * step, 0f, ox + i * step, h, i % 5 == 0 ? major : minor);
            first = -Mathf.CeilToInt(oy / step);
            for (int i = first; oy + i * step < h; i++)
                Gfx.Line(0f, oy + i * step, w, oy + i * step, i % 5 == 0 ? major : minor);

            // The outline, revealed with the progress; the pen tip is where it stops.
            float reveal = (c.Exiting ? 1f : Mathf.Clamp01(0.05f + 0.95f * c.Progress)) * _total;
            Color ink = c.Blended(new Color(0.82f, 0.93f, 1f), 0.9f);
            float penX = ox, penY = oy, drawn = 0f;
            for (int i = 0; i < _count && drawn < reveal; i++)
            {
                int k = i * 4;
                float len = Length(i), f = Mathf.Min(1f, (reveal - drawn) / len);
                float x0 = ox + _seg[k] * s, y0 = oy + _seg[k + 1] * s;
                penX = x0 + (_seg[k + 2] - _seg[k]) * s * f;
                penY = y0 + (_seg[k + 3] - _seg[k + 1]) * s * f;
                Gfx.Line(x0, y0, penX, penY, ink);
                drawn += len;
            }

            // Stage stations (dashed) and the height dimension, once the outline is mostly there.
            float detail = Mathf.Clamp01(reveal / _total * 1.6f - 0.6f);
            Color dash = c.Blended(new Color(0.7f, 0.86f, 1f), 0.45f * detail);
            for (int i = 0; i < Stations.Length; i++)
            {
                float y = oy + Stations[i] * s, half = 0.15f * s;
                for (float x = ox - half; x < ox + half; x += 9f)
                    Gfx.Line(x, y, Mathf.Min(x + 5f, ox + half), y, dash);
            }
            float dx = ox + 0.26f * s;
            Color dim = c.Blended(new Color(0.8f, 0.92f, 1f), 0.7f * detail);
            Gfx.Line(dx, oy, dx, oy + s, dim);
            Gfx.Line(dx - 6f, oy, dx + 6f, oy, dim);
            Gfx.Line(dx - 6f, oy + s, dx + 6f, oy + s, dim);
            Gfx.Line(dx, oy + s, dx - 4f, oy + s - 12f, dim); Gfx.Line(dx, oy + s, dx + 4f, oy + s - 12f, dim);
            Gfx.Line(dx, oy, dx - 4f, oy + 12f, dim); Gfx.Line(dx, oy, dx + 4f, oy + 12f, dim);
            float by = oy - 0.05f * s, bx0 = ox - 0.105f * s, bx1 = ox + 0.105f * s;
            Gfx.Line(bx0, by, bx1, by, dim);
            Gfx.Line(bx0, by - 6f, bx0, by + 6f, dim); Gfx.Line(bx1, by - 6f, bx1, by + 6f, dim);

            // Title block, bottom right.
            float tx0 = w * 0.70f, tx1 = w * 0.93f, ty0 = h * 0.06f, ty1 = h * 0.17f;
            Color frame = c.Blended(new Color(0.8f, 0.92f, 1f), 0.55f);
            Gfx.Line(tx0, ty0, tx1, ty0, frame); Gfx.Line(tx1, ty0, tx1, ty1, frame);
            Gfx.Line(tx1, ty1, tx0, ty1, frame); Gfx.Line(tx0, ty1, tx0, ty0, frame);
            Gfx.Line(tx0, (ty0 + ty1) * 0.5f, tx1, (ty0 + ty1) * 0.5f, frame);
            Gfx.Line(tx0 + (tx1 - tx0) * 0.62f, ty0, tx0 + (tx1 - tx0) * 0.62f, (ty0 + ty1) * 0.5f, frame);
            GL.End();

            if (reveal < _total && c.Additive())
                Gfx.Fan(penX, penY, h * 0.008f, h * 0.008f, 16, c.Glow(new Color(0.85f, 0.95f, 1f), 0.9f), Color.clear);
        }

        public override void DrawGui(EffectContext c)
        {
            float w = c.W, h = c.H, s = h * 0.70f, ox = w * 0.40f, oy = h * 0.14f;
            Color ink = new Color(0.82f, 0.93f, 1f, 0.8f);
            float size = h * 0.016f;
            c.Label(ox + 0.27f * s, oy + 0.5f * s, "14,8 m", ink, size);
            c.Label(ox - 0.02f * s, oy - 0.07f * s, "Ø 2,5 m", ink, size);
            float tx0 = w * 0.70f, tx1 = w * 0.93f, ty0 = h * 0.06f, ty1 = h * 0.17f;
            c.Label(tx0 + h * 0.01f, ty1 - h * 0.012f, c.Tr("#HD_BlueprintTitle"), ink, size * 1.15f);
            c.Label(tx0 + h * 0.01f, (ty0 + ty1) * 0.5f - h * 0.012f, c.Tr("#HD_BlueprintScale"), ink, size * 0.9f);
            c.Label(tx0 + (tx1 - tx0) * 0.62f + h * 0.01f, (ty0 + ty1) * 0.5f - h * 0.012f, c.Tr("#HD_BlueprintSheet"), ink, size * 0.9f);
        }
    }

    /// <summary>Orbital altimetry survey: contour lines appear under a scan line sweeping down the map.</summary>
    internal sealed class SurveyEffect : LoadingEffect
    {
        private const int Cols = 104, Rows = 58, Levels = 13;
        private readonly float[] _field = new float[(Cols + 1) * (Rows + 1)];

        public override string Name => "survey";
        public override Color Background => new Color(0.05f, 0.05f, 0.048f, 1f);

        public override void Step(EffectContext c)
        {
            float drift = c.T * 0.012f;
            for (int j = 0; j <= Rows; j++)
                for (int i = 0; i <= Cols; i++)
                    _field[j * (Cols + 1) + i] = Noise.Fbm(i * 0.075f + drift, j * 0.075f + 4.2f + drift * 0.4f, 4);
        }

        public override void Draw(EffectContext c)
        {
            float w = c.W, h = c.H, cw = w / Cols, ch = h / Rows;
            // The scan follows the load's progress, but goes down at least as a 3 s sweep that starts fast (ease-out: over half
            // the screen after 1 s): a scene change's progress barely moves and then jumps, which left a thin band of map over
            // a black screen.
            float sweep = 1f - (1f - Mathf.Clamp01(c.T / 3f)) * (1f - Mathf.Clamp01(c.T / 3f));
            float reveal = Mathf.Max(Mathf.Clamp01(c.Progress * 1.05f + 0.04f), sweep);
            float scanY = c.Exiting ? -10f : h * (1f - reveal);
            if (c.Blend())
            {
                GL.Begin(GL.LINES);
                for (int l = 0; l < Levels; l++)
                {
                    float level = 0.28f + 0.44f * l / (Levels - 1);
                    bool index = l % 4 == 0;
                    Color col = c.Blended(Gfx.Lerp(new Color(0.28f, 0.58f, 0.58f), new Color(0.9f, 0.74f, 0.46f), l / (float)(Levels - 1)), index ? 0.8f : 0.38f);
                    GL.Color(col);
                    for (int j = 0; j < Rows; j++)
                    {
                        float y = j * ch;
                        if (y + ch < scanY)
                            continue;
                        for (int i = 0; i < Cols; i++)
                            Cell(i, j, cw, ch, level);
                    }
                }
                GL.End();
            }
            if (scanY > 0f && c.Additive())
            {
                GL.Begin(GL.QUADS);
                Gfx.RectV(0f, scanY, w, scanY + h * 0.06f, c.Glow(new Color(0.5f, 0.9f, 0.85f), 0.25f), Color.clear);
                Gfx.Rect(0f, scanY - 1f, w, scanY + 1f, c.Glow(new Color(0.7f, 1f, 0.95f), 0.9f));
                GL.End();
            }
        }

        // Marching squares for one cell and one level; corners bottom-left, bottom-right, top-right, top-left.
        private void Cell(int i, int j, float cw, float ch, float level)
        {
            float a = _field[j * (Cols + 1) + i], b = _field[j * (Cols + 1) + i + 1];
            float cc = _field[(j + 1) * (Cols + 1) + i + 1], d = _field[(j + 1) * (Cols + 1) + i];
            int idx = (a > level ? 1 : 0) | (b > level ? 2 : 0) | (cc > level ? 4 : 0) | (d > level ? 8 : 0);
            if (idx == 0 || idx == 15)
                return;
            float x = i * cw, y = j * ch;
            switch (idx)
            {
                case 1: case 14: Seg(x, y, cw, ch, a, b, cc, d, level, 3, 0); break;
                case 2: case 13: Seg(x, y, cw, ch, a, b, cc, d, level, 0, 1); break;
                case 3: case 12: Seg(x, y, cw, ch, a, b, cc, d, level, 3, 1); break;
                case 4: case 11: Seg(x, y, cw, ch, a, b, cc, d, level, 1, 2); break;
                case 5: Seg(x, y, cw, ch, a, b, cc, d, level, 3, 2); Seg(x, y, cw, ch, a, b, cc, d, level, 0, 1); break;
                case 6: case 9: Seg(x, y, cw, ch, a, b, cc, d, level, 0, 2); break;
                case 7: case 8: Seg(x, y, cw, ch, a, b, cc, d, level, 3, 2); break;
                case 10: Seg(x, y, cw, ch, a, b, cc, d, level, 3, 0); Seg(x, y, cw, ch, a, b, cc, d, level, 1, 2); break;
            }
        }

        private static void Seg(float x, float y, float cw, float ch, float a, float b, float c, float d, float level, int e0, int e1)
        {
            Edge(x, y, cw, ch, a, b, c, d, level, e0, out float x0, out float y0);
            Edge(x, y, cw, ch, a, b, c, d, level, e1, out float x1, out float y1);
            GL.Vertex3(x0, y0, 0f);
            GL.Vertex3(x1, y1, 0f);
        }

        // Edges: 0 bottom (a-b), 1 right (b-c), 2 top (d-c), 3 left (a-d).
        private static void Edge(float x, float y, float cw, float ch, float a, float b, float c, float d, float level, int e, out float ex, out float ey)
        {
            switch (e)
            {
                case 0: ex = x + cw * T(a, b, level); ey = y; break;
                case 1: ex = x + cw; ey = y + ch * T(b, c, level); break;
                case 2: ex = x + cw * T(d, c, level); ey = y + ch; break;
                default: ex = x; ey = y + ch * T(a, d, level); break;
            }
        }

        private static float T(float v0, float v1, float level) => Mathf.Abs(v1 - v0) < 1e-6f ? 0.5f : Mathf.Clamp01((level - v0) / (v1 - v0));

        public override void DrawGui(EffectContext c)
        {
            float size = c.H * 0.015f;
            Color col = new Color(0.9f, 0.82f, 0.62f, 0.55f);
            c.Label(c.W * 0.93f, c.H * 0.93f, c.Tr("#HD_SurveyTitle"), col, size, true);
            c.Label(c.W * 0.93f, c.H * 0.93f - size * 1.6f, c.Tr("#HD_SurveyContour"), col, size * 0.9f, true);
        }
    }

    /// <summary>The navball: the sphere rolls and pitches up as the loading climbs; prograde marker and heading chevron.</summary>
    internal sealed class NavballEffect : LoadingEffect
    {
        private const int Rim = 128;
        private readonly Vector2[] _sky = new Vector2[Rim + 4], _ground = new Vector2[Rim + 4];

        public override string Name => "navball";
        public override Color Background => new Color(0.02f, 0.022f, 0.026f, 1f);

        public override void Draw(EffectContext c)
        {
            float cx = c.W * 0.5f, cy = c.H * 0.5f, R = c.H * 0.30f;
            float roll = (14f * Mathf.Sin(c.T * 0.27f) + 5f * Mathf.Sin(c.T * 0.83f + 1f)) * Mathf.Deg2Rad;
            float pitchDeg = 8f + 62f * c.Progress + 4f * Mathf.Sin(c.T * 0.31f);
            float pxPerDeg = R / 90f;
            Vector2 n = new Vector2(-Mathf.Sin(roll), Mathf.Cos(roll));      // up direction of the sphere
            Vector2 along = new Vector2(Mathf.Cos(roll), Mathf.Sin(roll));
            float offset = -pitchDeg * pxPerDeg;                                // the horizon drops as the nose rises

            int ns = 0, ng = 0;
            for (int i = 0; i <= Rim; i++)
            {
                float a = i * Gfx.Tau / Rim;
                Vector2 p = new Vector2(Mathf.Cos(a) * R, Mathf.Sin(a) * R);
                float side = Vector2.Dot(p, n) - offset;
                if (i > 0)
                {
                    float pa = (i - 1) * Gfx.Tau / Rim;
                    Vector2 q = new Vector2(Mathf.Cos(pa) * R, Mathf.Sin(pa) * R);
                    float qs = Vector2.Dot(q, n) - offset;
                    if ((qs > 0f) != (side > 0f))
                    {
                        Vector2 x = Vector2.Lerp(q, p, qs / (qs - side));
                        _sky[ns++] = x;
                        _ground[ng++] = x;
                    }
                }
                if (side > 0f) _sky[ns++] = p; else _ground[ng++] = p;
            }
            if (!c.Blend())
                return;
            FillConvex(cx, cy, _ground, ng, c.Blended(new Color(0.62f, 0.4f, 0.19f)), c.Blended(new Color(0.34f, 0.2f, 0.09f)));
            FillConvex(cx, cy, _sky, ns, c.Blended(new Color(0.27f, 0.55f, 0.82f)), c.Blended(new Color(0.12f, 0.28f, 0.48f)));

            GL.Begin(GL.LINES);
            Color line = c.Blended(Color.white, 0.75f), minor = c.Blended(Color.white, 0.35f);
            for (int k = -8; k <= 8; k++)
            {
                float off = offset + k * 10f * pxPerDeg;
                if (Mathf.Abs(off) >= R * 0.97f)
                    continue;
                float half = Mathf.Sqrt(R * R - off * off) * (k == 0 ? 1f : k % 3 == 0 ? 0.55f : 0.25f);
                Vector2 m = n * off;
                Gfx.Line(cx + m.x - along.x * half, cy + m.y - along.y * half, cx + m.x + along.x * half, cy + m.y + along.y * half, k == 0 ? line : minor);
            }
            // Bezel ticks every 15 degrees of heading.
            for (int i = 0; i < 24; i++)
            {
                float a = i * Gfx.Tau / 24f;
                float r0 = R * 1.03f, r1 = R * (i % 6 == 0 ? 1.11f : 1.07f);
                Gfx.Line(cx + Mathf.Cos(a) * r0, cy + Mathf.Sin(a) * r0, cx + Mathf.Cos(a) * r1, cy + Mathf.Sin(a) * r1, minor);
            }
            GL.End();
            Gfx.Ring(cx, cy, R, R * 1.025f, 96, c.Blended(new Color(0.16f, 0.17f, 0.19f)), c.Blended(new Color(0.3f, 0.31f, 0.33f)));

            // Prograde marker, wandering a little around the nose.
            float mx = cx + R * 0.16f * Mathf.Sin(c.T * 0.4f), my = cy + R * 0.12f * Mathf.Sin(c.T * 0.53f + 0.7f);
            Color pro = c.Blended(new Color(0.85f, 0.95f, 0.2f));
            float pr = R * 0.07f;
            GL.Begin(GL.LINES);
            Gfx.Arc(mx, my, pr, pr, 32, pro);
            Gfx.Line(mx, my + pr, mx, my + pr * 1.8f, pro);
            Gfx.Line(mx - pr, my, mx - pr * 1.8f, my, pro);
            Gfx.Line(mx + pr, my, mx + pr * 1.8f, my, pro);
            GL.End();
            // Fixed heading chevron.
            GL.Begin(GL.QUADS);
            Color chev = c.Blended(new Color(1f, 0.62f, 0.12f));
            float s = R * 0.16f;
            Gfx.Thick(cx - s, cy + s * 0.15f, cx - s * 0.3f, cy + s * 0.15f, R * 0.02f, chev);
            Gfx.Thick(cx - s * 0.3f, cy + s * 0.15f, cx, cy - s * 0.25f, R * 0.02f, chev);
            Gfx.Thick(cx, cy - s * 0.25f, cx + s * 0.3f, cy + s * 0.15f, R * 0.02f, chev);
            Gfx.Thick(cx + s * 0.3f, cy + s * 0.15f, cx + s, cy + s * 0.15f, R * 0.02f, chev);
            GL.End();
            if (c.Additive())
                Gfx.Fan(cx - R * 0.35f, cy + R * 0.4f, R * 0.7f, R * 0.55f, 32, c.Glow(Color.white, 0.12f), Color.clear);
        }

        // A circle cut by a line is convex: a fan from the centroid fills it, darker towards the rim.
        private static void FillConvex(float cx, float cy, Vector2[] pts, int count, Color centre, Color rim)
        {
            if (count < 3)
                return;
            Vector2 m = Vector2.zero;
            for (int i = 0; i < count; i++)
                m += pts[i];
            m /= count;
            GL.Begin(GL.TRIANGLES);
            for (int i = 0; i < count; i++)
            {
                Vector2 a = pts[i], b = pts[(i + 1) % count];
                GL.Color(centre); GL.Vertex3(cx + m.x, cy + m.y, 0f);
                GL.Color(rim); GL.Vertex3(cx + a.x, cy + a.y, 0f); GL.Vertex3(cx + b.x, cy + b.y, 0f);
            }
            GL.End();
        }
    }

    /// <summary>Pre-launch checklist typed out by mission control, statuses coming back one after another.</summary>
    internal sealed class ChecklistEffect : LoadingEffect
    {
        private const int CheckCount = 15;   // #HD_Check_1..15 in Localization/*.cfg
        private const float CharsPerSecond = 24f;
        private string[] _left, _right;
        private int _shownLine = -1, _shownChars = -1;
        private string _partial = "";
        private GUIStyle _mono;

        public override string Name => "checklist";
        public override Color Background => new Color(0.032f, 0.032f, 0.036f, 1f);

        public override void Init(EffectContext c, System.Random rng)
        {
            var src = new string[CheckCount];
            for (int i = 0; i < CheckCount; i++)
                src[i] = Lang.T("#HD_Check_" + (i + 1));   // "label|value"
            _left = new string[src.Length];
            _right = new string[src.Length];
            for (int i = 0; i < src.Length; i++)
            {
                int bar = src[i].IndexOf('|');
                _left[i] = src[i].Substring(0, bar);
                _right[i] = src[i].Substring(bar + 1);
            }
        }

        public override void Draw(EffectContext c)
        {
            if (!c.Blend())
                return;
            GL.Begin(GL.LINES);
            Color rule = c.Blended(new Color(0.85f, 0.82f, 0.7f), 0.18f);
            Gfx.Line(c.W * 0.2f, c.H * 0.84f, c.W * 0.8f, c.H * 0.84f, rule);
            Gfx.Line(c.W * 0.2f, c.H * 0.2f, c.W * 0.8f, c.H * 0.2f, rule);
            GL.End();
        }

        public override void DrawGui(EffectContext c)
        {
            if (_mono == null)
            {
                Font font = Font.CreateDynamicFontFromOSFont(new[] { "Consolas", "Courier New", "Menlo", "DejaVu Sans Mono" }, 16);
                _mono = new GUIStyle { font = font, richText = false, wordWrap = false };
                _mono.normal.textColor = Color.white;
            }
            float size = c.H * 0.02f, lineH = size * 1.7f;
            _mono.fontSize = Mathf.RoundToInt(size);
            int total = (int)(c.T * CharsPerSecond);
            if (c.Exiting)
                total = int.MaxValue / 2;
            float x = c.W * 0.2f, top = c.H * 0.84f - lineH * 1.1f, right = c.W * 0.8f;
            Color paper = new Color(0.88f, 0.86f, 0.78f, 1f);
            GUI.color = new Color(paper.r, paper.g, paper.b, 0.5f * c.Overlay);
            _mono.alignment = TextAnchor.UpperLeft;
            GUI.Label(new Rect(x, c.H - (c.H * 0.84f) + size * 0.4f, right - x, lineH), c.Tr("#HD_ChecklistTitle"), _mono);

            int maxLines = Mathf.FloorToInt((top - c.H * 0.2f) / lineH);
            int done = 0, line = 0, consumed = 0;
            while (line < _left.Length && consumed + _left[line].Length + 4 <= total)
            {
                consumed += _left[line].Length + 4;
                line++;
                done++;
            }
            int first = Mathf.Max(0, done + 1 - maxLines);
            for (int i = first; i <= done && i < _left.Length; i++)
            {
                float y = c.H - top + (i - first) * lineH;
                bool complete = i < done;
                string text = _left[i];
                if (!complete)
                {
                    int chars = Mathf.Clamp(total - consumed, 0, text.Length);
                    if (i != _shownLine || chars != _shownChars)
                    {
                        _shownLine = i;
                        _shownChars = chars;
                        _partial = text.Substring(0, chars) + ((int)(c.T * 2.5f) % 2 == 0 ? "_" : "");
                    }
                    text = _partial;
                }
                GUI.color = new Color(paper.r, paper.g, paper.b, 0.85f * c.Overlay);
                _mono.alignment = TextAnchor.UpperLeft;
                GUI.Label(new Rect(x, y, right - x, lineH), text, _mono);
                if (complete)
                {
                    string status = _right[i];
                    bool doubt = status.StartsWith("PROB");
                    GUI.color = doubt ? new Color(1f, 0.72f, 0.25f, 0.9f * c.Overlay) : new Color(0.45f, 0.95f, 0.55f, 0.9f * c.Overlay);
                    _mono.alignment = TextAnchor.UpperRight;
                    GUI.Label(new Rect(x, y, right - x, lineH), status, _mono);
                }
            }
        }
    }

}
