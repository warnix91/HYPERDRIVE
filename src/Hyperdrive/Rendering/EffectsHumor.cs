using Hyperdrive.Core;
using UnityEngine;

namespace Hyperdrive.Rendering
{
    /// <summary>A rotated local frame for parts that tumble. Box, BoxH, Trap and Tri go inside GL.QUADS.</summary>
    internal struct Pose
    {
        public readonly float X, Y, Angle, Cs, Sn;

        public Pose(float x, float y, float angle)
        {
            X = x; Y = y; Angle = angle; Cs = Mathf.Cos(angle); Sn = Mathf.Sin(angle);
        }

        public float Px(float lx, float ly) => X + lx * Cs - ly * Sn;

        public float Py(float lx, float ly) => Y + lx * Sn + ly * Cs;

        private void V(float lx, float ly) => Gfx.V(X + lx * Cs - ly * Sn, Y + lx * Sn + ly * Cs);

        public void Box(float x0, float y0, float x1, float y1, Color c)
        {
            GL.Color(c); V(x0, y0); V(x0, y1); V(x1, y1); V(x1, y0);
        }

        // Two flat tones, the shaded one on the left third: cel shading rather than a smooth gradient.
        public void BoxH(float x0, float y0, float x1, float y1, Color left, Color right)
        {
            float xm = x0 + (x1 - x0) * 0.36f;
            GL.Color(left); V(x0, y0); V(x0, y1); V(xm, y1); V(xm, y0);
            GL.Color(right); V(xm, y0); V(xm, y1); V(x1, y1); V(x1, y0);
        }

        // Trapezoid centred on x = 0, half widths at its bottom (y0) and top (y1), cel-shaded like BoxH.
        public void Trap(float y0, float y1, float half0, float half1, Color left, Color right)
        {
            float m0 = -half0 * 0.28f, m1 = -half1 * 0.28f;
            GL.Color(left); V(-half0, y0); V(-half1, y1); V(m1, y1); V(m0, y0);
            GL.Color(right); V(m0, y0); V(m1, y1); V(half1, y1); V(half0, y0);
        }

        // Ink line with square ends (corners close), inside GL.QUADS.
        public void Ink(float ax, float ay, float bx, float by, float width, Color c)
        {
            float dx = bx - ax, dy = by - ay, len = Mathf.Sqrt(dx * dx + dy * dy);
            if (len < 1e-4f)
                return;
            float ex = dx / len * width * 0.5f, ey = dy / len * width * 0.5f;
            Thick(ax - ex, ay - ey, bx + ex, by + ey, width, c);
        }

        public void InkBox(float x0, float y0, float x1, float y1, float width, Color c)
        {
            Ink(x0, y0, x1, y0, width, c); Ink(x1, y0, x1, y1, width, c);
            Ink(x1, y1, x0, y1, width, c); Ink(x0, y1, x0, y0, width, c);
        }

        public void InkTrap(float y0, float y1, float half0, float half1, float width, Color c)
        {
            Ink(-half0, y0, half0, y0, width, c); Ink(half0, y0, half1, y1, width, c);
            Ink(half1, y1, -half1, y1, width, c); Ink(-half1, y1, -half0, y0, width, c);
        }

        public void InkTri(float ax, float ay, float bx, float by, float cx, float cy, float width, Color c)
        {
            Ink(ax, ay, bx, by, width, c); Ink(bx, by, cx, cy, width, c); Ink(cx, cy, ax, ay, width, c);
        }

        public void Tri(float ax, float ay, float bx, float by, float cx, float cy, Color c)
        {
            GL.Color(c); V(ax, ay); V(bx, by); V(cx, cy); V(cx, cy);
        }

        public void Thick(float x0, float y0, float x1, float y1, float width, Color c) =>
            Gfx.Thick(Px(x0, y0), Py(x0, y0), Px(x1, y1), Py(x1, y1), width, c);
    }

    /// <summary>Shared drawing for the comic scenes: tank colours, rockets, the top of a planet.</summary>
    internal static class Comic
    {
        public static readonly Color Tank = new Color(0.88f, 0.88f, 0.9f), TankShade = new Color(0.42f, 0.43f, 0.47f);
        public static readonly Color Band = new Color(0.1f, 0.1f, 0.11f), Steel = new Color(0.45f, 0.46f, 0.5f);
        public static readonly Color Ink = new Color(0.07f, 0.06f, 0.06f);

        public static float InkWidth(float h) => Mathf.Max(1.5f, h * 0.0026f);

        // Blend pass set; own GL batch. The top of a large planet whose highest point is at height 'top', in one flat tone
        // with a darker band inside the rim.
        public static void Limb(EffectContext c, float top, Color centre, Color rim)
        {
            float r = c.W * 1.4f;
            Gfx.Fan(c.W * 0.5f, top - r, r, r, 120, rim, rim, Mathf.PI * 0.3f, Mathf.PI * 0.7f);
            Gfx.Fan(c.W * 0.5f, top - r, r - c.H * 0.035f, r - c.H * 0.035f, 120, centre, centre, Mathf.PI * 0.3f, Mathf.PI * 0.7f);
        }

        // Sets the blend pass; own GL batch. A thin flat band of atmosphere over Limb.
        public static void LimbGlow(EffectContext c, float top, Color glow, float strength)
        {
            if (!c.Blend())
                return;
            float r = c.W * 1.4f;
            Gfx.Ring(c.W * 0.5f, top - r, r, r + c.H * 0.012f, 120, c.Blended(glow, strength), c.Blended(glow, strength), Mathf.PI * 0.3f, Mathf.PI * 0.7f);
        }

        // Blend pass set: a puff of smoke in two flat opaque tones (shaded underside, lit top). It fades by shrinking, like
        // cartoon smoke, so overlapping puffs merge into one cloud instead of showing stacked translucent circles.
        public static void Puff(EffectContext c, float x, float y, float r, Color col, float alpha)
        {
            if (alpha <= 0.02f)
                return;
            r *= Mathf.Sqrt(Mathf.Clamp01(alpha));
            Color shade = new Color(col.r * 0.8f, col.g * 0.8f, col.b * 0.82f);
            Gfx.Fan(x, y - r * 0.12f, r, r, 14, c.Blended(shade), c.Blended(shade));
            Gfx.Fan(x - r * 0.1f, y + r * 0.06f, r * 0.8f, r * 0.8f, 14, c.Blended(col), c.Blended(col));
        }

        // Inside GL.QUADS: a plain rocket in its own frame, engine at the origin. Height about 0.31 h.
        public static void Rocket(EffectContext c, Pose p, float half, float h, float alpha)
        {
            Color fin = c.Blended(Steel, alpha);
            p.Tri(-half, h * 0.07f, -half * 2.1f, -h * 0.004f, -half, -h * 0.004f, fin);
            p.Tri(half, h * 0.07f, half, -h * 0.004f, half * 2.1f, -h * 0.004f, fin);
            p.Trap(-h * 0.014f, 0f, half * 0.8f, half * 0.45f, c.Blended(new Color(0.12f, 0.12f, 0.13f), alpha), c.Blended(new Color(0.3f, 0.3f, 0.32f), alpha));
            p.BoxH(-half, 0f, half, h * 0.26f, c.Blended(TankShade, alpha), c.Blended(Tank, alpha));
            p.Box(-half, h * 0.1f, half, h * 0.108f, c.Blended(Band, alpha));
            p.Box(-half, h * 0.252f, half, h * 0.262f, c.Blended(Band, alpha));
            Color ink = c.Blended(Ink, alpha);
            float iw = InkWidth(h);
            p.InkTri(-half, h * 0.07f, -half * 2.1f, -h * 0.004f, -half, -h * 0.004f, iw, ink);
            p.InkTri(half, h * 0.07f, half, -h * 0.004f, half * 2.1f, -h * 0.004f, iw, ink);
            p.InkTrap(-h * 0.014f, 0f, half * 0.8f, half * 0.45f, iw, ink);
            p.InkBox(-half, 0f, half, h * 0.262f, iw, ink);
            Capsule(c, new Pose(p.Px(0f, h * 0.262f), p.Py(0f, h * 0.262f), p.Angle), half, h, alpha);
        }

        // Inside GL.QUADS: the Mk1 pod, base at the origin.
        public static void Capsule(EffectContext c, Pose p, float half, float h, float alpha)
        {
            p.Box(-half * 1.02f, -h * 0.006f, half * 1.02f, 0f, c.Blended(new Color(0.32f, 0.22f, 0.14f), alpha));
            p.Trap(0f, h * 0.05f, half, half * 0.4f, c.Blended(new Color(0.26f, 0.27f, 0.29f), alpha), c.Blended(new Color(0.62f, 0.63f, 0.66f), alpha));
            p.Box(-half * 0.2f, h * 0.018f, half * 0.24f, h * 0.03f, c.Blended(new Color(0.07f, 0.1f, 0.16f), alpha));
            p.InkTrap(-h * 0.006f, h * 0.05f, half * 1.02f, half * 0.4f, InkWidth(h), c.Blended(Ink, alpha));
        }
    }

    /// <summary>A tall rocket wobbling like a wet noodle; struts get added until it stands still.</summary>
    internal sealed class StrutsEffect : LoadingEffect
    {
        private const int Segments = 9, MaxStruts = 48;
        private readonly float[] _sx = new float[Segments + 1], _sy = new float[Segments + 1];
        private int _shown = -1;
        private string _count = "";

        public override string Name => "struts";
        public override float Boil => 1.6f;
        public override Color Background => new Color(0.03f, 0.035f, 0.05f, 1f);

        private static int Struts(EffectContext c) => c.Exiting ? MaxStruts : 2 + Mathf.FloorToInt(Mathf.Clamp01(c.Progress) * (MaxStruts - 2));

        public override void Draw(EffectContext c)
        {
            float w = c.W, h = c.H, ground = h * 0.16f, cx = w * 0.4f, seg = h * 0.068f, half = h * 0.026f;
            int n = Struts(c);
            float loose = 1f - (float)n / MaxStruts;
            float amp = h * (0.13f * loose * loose + 0.003f);
            for (int k = 0; k <= Segments; k++)
            {
                float f = (float)k / Segments;
                _sx[k] = cx + amp * f * f * Mathf.Sin(c.T * 2.2f - f * 1.3f) + amp * 0.2f * f * Mathf.Sin(c.T * 4.7f + k);
                _sy[k] = ground + h * 0.02f + seg * k;
            }
            if (!c.Blend())
                return;
            GL.Begin(GL.QUADS);
            Gfx.Bands(0f, ground, w, h, c.Blended(new Color(0.2f, 0.24f, 0.31f)), c.Blended(new Color(0.05f, 0.06f, 0.09f)), 5);
            Gfx.Bands(0f, 0f, w, ground, c.Blended(new Color(0.07f, 0.075f, 0.08f)), c.Blended(new Color(0.15f, 0.155f, 0.16f)), 3);
            Gfx.Rect(cx - h * 0.1f, ground - h * 0.006f, cx + h * 0.1f, ground + h * 0.02f, c.Blended(new Color(0.3f, 0.3f, 0.31f)));
            // Rocket: one lit tank per segment, bent along the wobble.
            for (int k = 0; k < Segments; k++)
            {
                float mx = (_sx[k] + _sx[k + 1]) * 0.5f, my = (_sy[k] + _sy[k + 1]) * 0.5f;
                var p = new Pose(mx, my, -Mathf.Atan2(_sx[k + 1] - _sx[k], seg));
                p.BoxH(-half, -seg * 0.5f - 1f, half, seg * 0.5f + 1f, c.Blended(Comic.TankShade), c.Blended(Comic.Tank));
                if (k % 3 == 2)
                    p.Box(-half, seg * 0.42f, half, seg * 0.5f, c.Blended(Comic.Band));
                p.InkBox(-half, -seg * 0.5f, half, seg * 0.5f, Comic.InkWidth(h), c.Blended(Comic.Ink));
                if (k == 0)
                {
                    p.Trap(-seg * 0.5f - h * 0.016f, -seg * 0.5f, half * 0.8f, half * 0.45f, c.Blended(new Color(0.12f, 0.12f, 0.13f)), c.Blended(new Color(0.3f, 0.3f, 0.32f)));
                    p.Tri(-half, -seg * 0.1f, -half * 2f, -seg * 0.5f, -half, -seg * 0.5f, c.Blended(Comic.Steel));
                    p.Tri(half, -seg * 0.1f, half, -seg * 0.5f, half * 2f, -seg * 0.5f, c.Blended(Comic.Steel));
                }
                if (k == Segments - 1)
                    Comic.Capsule(c, new Pose(p.Px(0f, seg * 0.5f), p.Py(0f, seg * 0.5f), p.Angle), half, h, 1f);
            }
            // Struts: guy lines to the ground on both sides, then braces across the stack. Messier as they pile up.
            Color strut = c.Blended(new Color(0.3f, 0.31f, 0.34f)), cap = c.Blended(new Color(0.9f, 0.72f, 0.22f));
            for (int i = 0; i < n; i++)
            {
                float side = (i & 1) == 0 ? -1f : 1f, x0, y0, x1, y1;
                if (i % 3 != 2)
                {
                    int k = 1 + (i * 5) % (Segments - 1);
                    x0 = _sx[k] + side * half;
                    y0 = _sy[k] + seg * (0.2f + 0.6f * Noise.Hash(i, 3));
                    x1 = cx + side * h * (0.12f + 0.55f * Noise.Hash(i, 7));
                    y1 = ground;
                }
                else
                {
                    int k = (i * 7) % (Segments - 1);
                    x0 = _sx[k] - side * half;
                    y0 = _sy[k] + seg * 0.3f;
                    x1 = _sx[k + 1] + side * half;
                    y1 = _sy[k + 1] + seg * 0.7f;
                }
                Gfx.Thick(x0, y0, x1, y1, Mathf.Max(1.5f, h * 0.0022f), strut);
                float s = h * 0.004f;
                Gfx.Rect(x0 - s, y0 - s, x0 + s, y0 + s, cap);
                Gfx.Rect(x1 - s, y1 - s, x1 + s, y1 + s, cap);
            }
            GL.End();
        }

        public override void DrawGui(EffectContext c)
        {
            int n = Struts(c);
            if (n != _shown)
            {
                _shown = n;
                _count = "STRUTS  " + n;
            }
            c.Label(c.W * 0.93f, c.H * 0.92f, _count, new Color(0.9f, 0.88f, 0.8f, 0.8f), c.H * 0.026f, true);   // top right: the load problems notice takes the top left
        }
    }

    /// <summary>Launch, a few seconds of hope, then a rapid unplanned disassembly. The pod always gets away. Repeat.</summary>
    internal sealed class RudEffect : LoadingEffect
    {
        private const float Cycle = 7f, Ignite = 0.7f, Boom = 2.6f, ChuteAt = 0.9f;
        private const int Parts = 7, Sparks = 40, Smoke = 14;
        private float _t0;
        private int _shownCycle = -1;
        private string _trial = "";

        public override string Name => "rud";
        public override float Boil => 1.6f;
        public override Color Background => new Color(0.4f, 0.5f, 0.62f, 1f);

        public override void Init(EffectContext c, System.Random rng)
        {
            _t0 = c.T;
            _shownCycle = -1;
        }

        public override void Draw(EffectContext c)
        {
            float w = c.W, h = c.H, ground = h * 0.2f, cx = w * 0.56f, half = h * 0.024f, g = h * 1.1f;
            float lt = c.T - _t0;
            int cycle = Mathf.FloorToInt(lt / Cycle);
            float t = lt - cycle * Cycle;
            float side = Noise.Hash(cycle, 5) < 0.5f ? -1f : 1f;
            float rise = t < Ignite ? 0f : Mathf.Min(1f, (t - Ignite) / (Boom - Ignite));
            float rx = cx + side * h * 0.12f * rise * rise * rise, ry = ground + h * 0.38f * rise * rise;
            float tilt = -side * 0.4f * rise * rise * rise + 0.03f * Mathf.Sin(t * 17f) * rise;
            if (!c.Blend())
                return;
            GL.Begin(GL.QUADS);
            MemeBits.Daylight(c, ground, cx);
            if (t < Boom)
                Comic.Rocket(c, new Pose(rx, ry, tilt), half, h, 1f);
            GL.End();
            // Exhaust smoke rolling off the pad.
            for (int i = 0; i < Smoke; i++)
            {
                float age = t - Ignite - Noise.Hash(i, cycle) * 0.8f;
                if (age <= 0f)
                    continue;
                float spread = (Noise.Hash(cycle, i) - 0.5f) * 2f;
                float x = cx + spread * h * 0.3f * Mathf.Min(1f, age * 0.7f), y = ground + h * 0.02f + h * 0.025f * age * Noise.Hash(i, 9);
                float r = h * (0.03f + 0.035f * Mathf.Min(age, 2.5f)), a = 0.85f * Mathf.Clamp01(1f - age / 5f);
                Comic.Puff(c, x, y, r, new Color(0.92f, 0.92f, 0.9f), a);
            }
            if (t < Boom)
            {
                if (t > Ignite - 0.2f)
                {
                    var p = new Pose(rx, ry, tilt);
                    float k = Mathf.Clamp01((t - Ignite + 0.2f) / 0.3f), fl = 0.85f + 0.15f * Noise.Value(t * 15f, 7f);
                    MemeBits.Flame(c, p.Px(0f, -h * 0.014f), p.Py(0f, -h * 0.014f), -Mathf.PI * 0.5f + tilt, h * 0.12f * k * fl, 0.2f, 1f);
                }
                return;
            }
            float e = t - Boom, ex = rx, ey = ry + h * 0.14f;
            // Smoke of the blast, drifting up.
            if (c.Blend())
                for (int i = 0; i < 9; i++)
                {
                    float ox = (Noise.Hash(i, cycle + 40) - 0.5f) * h * 0.16f * (0.4f + e), oy = (Noise.Hash(cycle + 40, i) - 0.5f) * h * 0.12f + h * 0.03f * e;
                    float r = h * (0.04f + 0.07f * Mathf.Sqrt(e)), a = 0.85f * Mathf.Clamp01(1f - e / 4f) * Mathf.Clamp01(e * 3f);
                    Comic.Puff(c, ex + ox, ey + oy, r, new Color(0.36f, 0.35f, 0.34f), a);
                }
            // Pieces: ballistic, spinning, stopped by the ground.
            if (c.Blend())
            {
                GL.Begin(GL.QUADS);
                for (int i = 0; i < Parts; i++)
                {
                    float vx = (Noise.Hash(i, cycle) - 0.5f) * h * 0.9f, vy = (0.25f + 0.75f * Noise.Hash(cycle, i + 17)) * h * 0.6f;
                    float spin = (Noise.Hash(i + 31, cycle) - 0.5f) * 14f;
                    float hit = (vy + Mathf.Sqrt(vy * vy + 2f * g * (ey - ground))) / g, te = Mathf.Min(e, hit);
                    Part(c, i, new Pose(ex + vx * te, ey + vy * te - 0.5f * g * te * te, tilt + spin * te), half, h);
                }
                // The pod, lifted clear by its escape motor, then under its chute.
                float vy0 = h * 0.55f, py, k = Mathf.Clamp01((e - ChuteAt) / 0.35f);
                if (e < ChuteAt)
                    py = ey + vy0 * e - 0.5f * g * e * e;
                else
                {
                    float at = ey + vy0 * ChuteAt - 0.5f * g * ChuteAt * ChuteAt, v = vy0 - g * ChuteAt, slow = -h * 0.06f, s = e - ChuteAt;
                    py = at + slow * s + (v - slow) * (1f - Mathf.Exp(-6f * s)) / 6f;
                }
                py = Mathf.Max(py, ground + h * 0.006f);
                float px = ex - side * h * 0.05f * e, pa = 0.12f * Mathf.Sin(e * 2.2f) * k + tilt * (1f - k);
                var pod = new Pose(px, py, pa);
                Comic.Capsule(c, pod, half, h, 1f);
                GL.End();
                if (k > 0f)
                    Chute(c, pod, h, k);
            }
            if (e < 1.3f && c.Blend())
            {
                float k = e / 1.3f;
                if (e < 0.9f)
                {
                    // A cartoon blast: three flat discs, red to white, shrinking from the inside out.
                    float r = h * (0.05f + 0.2f * Mathf.Sqrt(e)), fade = 1f - e / 0.9f;
                    Color red = new Color(0.9f, 0.32f, 0.1f), yellow = new Color(1f, 0.78f, 0.25f), white = new Color(1f, 0.97f, 0.85f);
                    Gfx.Fan(ex, ey, r, r, 18, c.Blended(red, fade), c.Blended(red, fade));
                    Gfx.Fan(ex + r * 0.06f, ey + r * 0.05f, r * 0.68f * fade, r * 0.68f * fade, 16, c.Blended(yellow, fade), c.Blended(yellow, fade));
                    Gfx.Fan(ex - r * 0.04f, ey + r * 0.1f, r * 0.34f * fade, r * 0.34f * fade, 12, c.Blended(white, fade), c.Blended(white, fade));
                }
                GL.Begin(GL.QUADS);
                for (int i = 0; i < Sparks; i++)
                {
                    float a = Noise.Hash(i, cycle + 7) * Gfx.Tau, sp = h * (0.3f + 0.7f * Noise.Hash(cycle + 7, i));
                    float x = ex + Mathf.Cos(a) * sp * e, y = ey + Mathf.Sin(a) * sp * e - 0.2f * g * e * e, s = h * 0.003f;
                    Gfx.Rect(x - s, y - s, x + s, y + s, c.Blended(new Color(1f, 0.62f, 0.2f), 1f - k));
                }
                GL.End();
            }
        }

        // Inside GL.QUADS: one piece of the late rocket.
        private static void Part(EffectContext c, int i, Pose p, float half, float h)
        {
            switch (i)
            {
                case 0:
                case 1:
                    p.BoxH(-half, -h * 0.05f, half, h * 0.05f, c.Blended(Comic.TankShade), c.Blended(Comic.Tank));
                    p.Box(-half, h * 0.04f, half, h * 0.048f, c.Blended(Comic.Band));
                    break;
                case 2:
                    p.Tri(0f, h * 0.035f, -half, -h * 0.035f, 0f, -h * 0.035f, c.Blended(Comic.Steel));
                    break;
                case 3:
                    p.Tri(0f, h * 0.035f, 0f, -h * 0.035f, half, -h * 0.035f, c.Blended(Comic.Steel));
                    break;
                case 4:
                    p.Trap(-h * 0.008f, h * 0.008f, half * 0.8f, half * 0.45f, c.Blended(new Color(0.12f, 0.12f, 0.13f)), c.Blended(new Color(0.3f, 0.3f, 0.32f)));
                    break;
                case 5:
                    p.BoxH(-half, -h * 0.025f, half, h * 0.025f, c.Blended(Comic.TankShade), c.Blended(Comic.Tank));
                    break;
                default:
                    p.Box(-half, -h * 0.004f, half, h * 0.004f, c.Blended(Comic.Band));
                    break;
            }
        }

        // Blend pass set: shroud lines and a striped canopy above the pod.
        private static void Chute(EffectContext c, Pose pod, float h, float k)
        {
            float top = h * 0.05f, lift = h * 0.13f * k, r = h * 0.075f * k;
            float qx = pod.Px(0f, top + lift), qy = pod.Py(0f, top + lift);
            GL.Begin(GL.LINES);
            Color line = c.Blended(new Color(0.85f, 0.85f, 0.82f), 0.7f);
            Gfx.Line(pod.Px(0f, top), pod.Py(0f, top), pod.Px(-r, top + lift), pod.Py(-r, top + lift), line);
            Gfx.Line(pod.Px(0f, top), pod.Py(0f, top), pod.Px(r, top + lift), pod.Py(r, top + lift), line);
            Gfx.Line(pod.Px(0f, top), pod.Py(0f, top), qx, qy, line);
            GL.End();
            for (int s = 0; s < 6; s++)
            {
                float a0 = pod.Angle + Mathf.PI * s / 6f, a1 = a0 + Mathf.PI / 6f;
                Color col = (s & 1) == 0 ? new Color(0.95f, 0.5f, 0.15f) : new Color(0.95f, 0.94f, 0.9f);
                Gfx.Fan(qx, qy, r, r * 0.6f, 6, c.Blended(col), c.Blended(col * 0.85f + new Color(0f, 0f, 0f, 0.15f)), a0, a1);
            }
        }

        public override void DrawGui(EffectContext c)
        {
            float lt = c.T - _t0;
            int cycle = Mathf.FloorToInt(lt / Cycle);
            if (cycle != _shownCycle)
            {
                _shownCycle = cycle;
                _trial = Lang.F("#HD_Trial", 12 + cycle);
            }
            c.Label(c.W * 0.93f, c.H * 0.92f, _trial, new Color(1f, 1f, 1f, 0.75f), c.H * 0.026f, true);   // top right: the load problems notice takes the top left
        }
    }

    /// <summary>A rover crosses the Minmus flats, takes a rock a little fast, flips over and lands on its roof, wheels spinning.</summary>
    internal sealed class RoverEffect : LoadingEffect
    {
        private const float Cycle = 8f, Climb = 2.6f, Launch = 3f, Fly = 1.01f;
        private const float Scale = 1.6f, Road = 0.16f, RockAt = 0.5f;     // rover size (in screen heights / 1), its track, the rock
        private float _t0;

        public override string Name => "rover";
        public override float Boil => 1.6f;
        public override Color Background => new Color(0f, 0f, 0.004f, 1f);

        public override void Init(EffectContext c, System.Random rng) => _t0 = c.T;

        // Where the rover is at time t of the loop (pixels, radians), how fast its wheels turn, and the phase (0 driving, 1 flying, 2 on its roof).
        private static void State(EffectContext c, float t, out float x, out float y, out float ang, out float wheel, out int phase)
        {
            float h = c.H * Scale, gy = c.H * Road, r = h * 0.02f, up = gy + r + h * 0.005f, down = gy + h * 0.031f;
            float x0 = -h * 0.12f, rock = c.W * RockAt, v = (rock - h * 0.03f - x0) / Launch;
            if (t < Launch)
            {
                phase = 0;
                x = x0 + v * t;
                float u = Mathf.Clamp01((t - Climb) / (Launch - Climb));
                y = up + h * 0.003f * Mathf.Sin(x * 0.05f) + h * 0.035f * u;
                ang = 0.03f * Mathf.Sin(t * 3f) + 0.4f * u;
                wheel = -x / r;
                return;
            }
            float xb = x0 + v * Launch, yb = up + h * 0.035f, vx = v * 1.3f, s = t - Launch;
            if (s < Fly)
            {
                phase = 1;
                x = xb + vx * s;
                y = yb + h * 0.35f * s - 0.375f * h * s * s;
                ang = 0.4f + (Mathf.PI - 0.4f) * s / Fly;
                wheel = -(xb + vx * s) / r;
                return;
            }
            phase = 2;
            s -= Fly;
            float decay = Mathf.Exp(-3f * s);
            x = xb + vx * Fly + vx * 0.15f * (1f - Mathf.Exp(-5f * s));
            y = down + h * 0.02f * Mathf.Abs(Mathf.Sin(s * 8f)) * Mathf.Exp(-4f * s);
            ang = Mathf.PI + 0.08f * Mathf.Sin(s * 9f) * decay;
            // Wheels keep spinning in the air, slowing down but never quite stopping.
            wheel = -(xb + vx * Fly) / r - (2f * s + 2.5f * (1f - decay));
        }

        public override void Draw(EffectContext c)
        {
            float w = c.W, sh = c.H, h = sh * Scale, gy = sh * Road, horizon = sh * 0.3f, r = h * 0.02f, lt = c.T - _t0;
            int loop = Mathf.FloorToInt(lt / Cycle);
            float t = lt - loop * Cycle, alpha = Mathf.Clamp01(t / 0.4f) * Mathf.Clamp01((Cycle - t) / 0.6f);
            State(c, t, out float x, out float y, out float ang, out float wheel, out int phase);
            if (c.Additive())
            {
                GL.Begin(GL.QUADS);
                SceneBits.Stars(c, 240, 29, horizon + sh * 0.1f, 0.7f);
                GL.End();
            }
            if (!c.Blend())
                return;
            GL.Begin(GL.QUADS);
            SceneBits.Ridge(c, horizon + sh * 0.07f, sh * 0.1f, 0.003f, 8.7f, c.Blended(new Color(0.3f, 0.4f, 0.38f)));
            Gfx.Bands(0f, 0f, w, horizon, c.Blended(new Color(0.42f, 0.55f, 0.5f)), c.Blended(new Color(0.64f, 0.78f, 0.72f)), 4);
            GL.End();
            // The rock it should have gone around.
            float rock = w * RockAt;
            Gfx.Fan(rock + h * 0.01f, gy, h * 0.045f, h * 0.03f, 20, c.Blended(new Color(0.5f, 0.56f, 0.54f)), c.Blended(new Color(0.3f, 0.36f, 0.35f)), 0f, Mathf.PI);
            // Dust where it came down.
            if (phase == 2)
            {
                float s = t - Launch - Fly, k = Mathf.Clamp01(s / 1.4f);
                for (int i = 0; i < 6; i++)
                {
                    float ox = (Noise.Hash(i, 13) - 0.5f) * h * 0.2f * (0.3f + k), rr = h * (0.02f + 0.05f * k);
                    Comic.Puff(c, x + ox, gy + h * 0.01f + h * 0.02f * k, rr * 0.8f, new Color(0.8f, 0.88f, 0.84f), 0.7f * (1f - k));
                }
            }
            var p = new Pose(x, y, ang);
            Color body = new Color(0.82f, 0.82f, 0.8f), dark = new Color(0.12f, 0.12f, 0.13f);
            // Wheels behind the chassis edge: tyre, three spokes turning, hub.
            for (int side = -1; side <= 1; side += 2)
            {
                float wx = p.Px(side * h * 0.055f, -h * 0.005f), wy = p.Py(side * h * 0.055f, -h * 0.005f);
                Gfx.Fan(wx, wy, r, r, 20, c.Blended(new Color(0.2f, 0.2f, 0.21f), alpha), c.Blended(dark, alpha));
                GL.Begin(GL.QUADS);
                for (int k = 0; k < 3; k++)
                {
                    float a = wheel + k * Mathf.PI / 3f, ca = Mathf.Cos(a) * r * 0.75f, sa = Mathf.Sin(a) * r * 0.75f;
                    Gfx.Thick(wx - ca, wy - sa, wx + ca, wy + sa, Mathf.Max(1.5f, h * 0.003f), c.Blended(new Color(0.45f, 0.46f, 0.48f), alpha));
                }
                GL.End();
                Gfx.Fan(wx, wy, r * 0.3f, r * 0.3f, 10, c.Blended(new Color(0.6f, 0.61f, 0.63f), alpha), c.Blended(new Color(0.4f, 0.41f, 0.43f), alpha));
            }
            GL.Begin(GL.QUADS);
            p.Thick(-h * 0.055f, -h * 0.005f, -h * 0.04f, h * 0.008f, h * 0.006f, c.Blended(Comic.Steel, alpha));
            p.Thick(h * 0.055f, -h * 0.005f, h * 0.04f, h * 0.008f, h * 0.006f, c.Blended(Comic.Steel, alpha));
            p.BoxH(-h * 0.07f, h * 0.004f, h * 0.07f, h * 0.024f, c.Blended(body * 0.55f + new Color(0f, 0f, 0f, 0.45f), alpha), c.Blended(body, alpha));
            p.Box(-h * 0.07f, h * 0.004f, h * 0.07f, h * 0.008f, c.Blended(Comic.Band, alpha));
            p.InkBox(-h * 0.07f, h * 0.004f, h * 0.07f, h * 0.024f, Comic.InkWidth(c.H), c.Blended(Comic.Ink, alpha));
            // Probe core, a solar panel and a whip antenna on the deck.
            p.Box(h * 0.03f, h * 0.024f, h * 0.06f, h * 0.036f, c.Blended(new Color(0.3f, 0.31f, 0.33f), alpha));
            p.Box(h * 0.036f, h * 0.028f, h * 0.054f, h * 0.032f, c.Blended(new Color(0.2f, 0.75f, 0.35f), alpha));
            p.Box(-h * 0.06f, h * 0.024f, -h * 0.004f, h * 0.029f, c.Blended(new Color(0.12f, 0.2f, 0.42f), alpha));
            p.Thick(-h * 0.03f, h * 0.024f, -h * 0.03f, h * 0.03f, Mathf.Max(1.5f, h * 0.003f), c.Blended(Comic.Steel, alpha));
            p.Thick(h * 0.064f, h * 0.024f, h * 0.072f, h * 0.07f, Mathf.Max(1.2f, h * 0.002f), c.Blended(Comic.Steel, alpha));
            GL.End();
        }

    }

    /// <summary>One more pair of boosters every time the bar moves. The camera backs off to fit them all.</summary>
    internal sealed class BoostersEffect : LoadingEffect
    {
        private const int MaxPairs = 24;
        private readonly float[] _attach = new float[MaxPairs + 1];
        private int _pairs, _shown = -1;
        private float _zoom = 1f;
        private string _count = "";

        public override string Name => "boosters";
        public override float Boil => 1.6f;
        public override Color Background => new Color(0.05f, 0.06f, 0.09f, 1f);

        public override void Init(EffectContext c, System.Random rng)
        {
            _pairs = 0;
            _zoom = 1f;
            for (int i = 0; i < _attach.Length; i++)
                _attach[i] = -10f;
        }

        private static float CoreHalf(float h) => h * 0.032f;
        private static float BoosterHalf(float h) => h * 0.02f;
        private static float Gap(float h) => h * 0.004f;
        private static float PairOffset(int k, float h) => CoreHalf(h) + Gap(h) + BoosterHalf(h) + (k - 1) * (2f * BoosterHalf(h) + Gap(h));

        public override void Step(EffectContext c)
        {
            int target = c.Exiting ? MaxPairs : 1 + Mathf.FloorToInt(Mathf.Clamp01(c.Progress) * (MaxPairs - 1));
            while (_pairs < target)
            {
                _pairs++;
                // Several at once (a jump in progress) snap on one after the other.
                _attach[_pairs] = _pairs > 1 ? Mathf.Max(c.T, _attach[_pairs - 1] + 0.2f) : c.T;
            }
            float needed = PairOffset(Mathf.Max(1, _pairs), c.H) + BoosterHalf(c.H) * 1.5f;
            float zoom = Mathf.Min(1f, c.W * 0.44f / needed);
            _zoom = Mathf.Lerp(_zoom, zoom, 1f - Mathf.Exp(-3f * c.Dt));
        }

        public override void Draw(EffectContext c)
        {
            float w = c.W, h = c.H, ground = h * 0.15f, cx = w * 0.5f, z = _zoom;
            if (!c.Blend())
                return;
            GL.Begin(GL.QUADS);
            Gfx.Bands(0f, ground, w, h, c.Blended(new Color(0.42f, 0.46f, 0.54f)), c.Blended(new Color(0.1f, 0.13f, 0.2f)), 5);
            Gfx.Bands(0f, 0f, w, ground, c.Blended(new Color(0.09f, 0.1f, 0.09f)), c.Blended(new Color(0.18f, 0.2f, 0.17f)), 3);
            float padHalf = (PairOffset(Mathf.Max(1, _pairs), h) + h * 0.06f) * z;
            Gfx.Rect(cx - padHalf, ground - h * 0.008f, cx + padHalf, ground + h * 0.008f * z, c.Blended(new Color(0.36f, 0.36f, 0.37f)));
            float baseY = ground + h * 0.008f * z, bh = BoosterHalf(h) * z, ch = CoreHalf(h) * z;
            Color dark = c.Blended(new Color(0.12f, 0.12f, 0.13f));
            for (int k = 1; k <= _pairs; k++)
            {
                float s = Mathf.Clamp01((c.T - _attach[k]) / 0.35f);
                if (s <= 0f)
                    continue;
                float drop = (1f - s) * (1f - s) * h * 0.6f, height = Mathf.Max(0.2f, 0.4f - 0.008f * k) * h * z, off = PairOffset(k, h) * z;
                float y0 = baseY + h * 0.012f * z + drop, y1 = y0 + height;
                for (int side = -1; side <= 1; side += 2)
                {
                    float x = cx + side * off;
                    Gfx.Quad(x - bh * 0.7f, y0 - h * 0.012f * z, x - bh * 0.45f, y0, x + bh * 0.45f, y0, x + bh * 0.7f, y0 - h * 0.012f * z, dark);
                    var q = new Pose(0f, 0f, 0f);
                    q.BoxH(x - bh, y0, x + bh, y1, c.Blended(Comic.TankShade, s), c.Blended(Comic.Tank, s));
                    q.InkBox(x - bh, y0, x + bh, y1, Comic.InkWidth(h * z), c.Blended(Comic.Ink, s));
                    Gfx.Rect(x - bh, y1 - height * 0.16f, x + bh, y1 - height * 0.12f, c.Blended(Comic.Band, s));
                    Gfx.Rect(x - bh, y0 + height * 0.1f, x + bh, y0 + height * 0.13f, c.Blended(Comic.Band, s));
                    Gfx.Quad(x - bh, y1, x, y1 + bh * 2.2f, x, y1 + bh * 2.2f, x + bh, y1, c.Blended(new Color(0.55f, 0.56f, 0.6f), s));
                    // Radial decouplers to the next booster in.
                    float inner = x - side * (bh + Gap(h) * z);
                    Gfx.Rect(Mathf.Min(inner, x - side * bh), y0 + height * 0.3f, Mathf.Max(inner, x - side * bh), y0 + height * 0.33f, c.Blended(new Color(0.25f, 0.25f, 0.27f), s));
                    Gfx.Rect(Mathf.Min(inner, x - side * bh), y0 + height * 0.75f, Mathf.Max(inner, x - side * bh), y0 + height * 0.78f, c.Blended(new Color(0.25f, 0.25f, 0.27f), s));
                }
            }
            // Core stage and pod.
            float coreTop = baseY + h * 0.5f * z;
            Gfx.Quad(cx - ch * 0.75f, baseY - h * 0.002f, cx - ch * 0.45f, baseY + h * 0.016f * z, cx + ch * 0.45f, baseY + h * 0.016f * z, cx + ch * 0.75f, baseY - h * 0.002f, dark);
            var core = new Pose(0f, 0f, 0f);
            core.BoxH(cx - ch, baseY + h * 0.016f * z, cx + ch, coreTop, c.Blended(Comic.TankShade), c.Blended(Comic.Tank));
            core.InkBox(cx - ch, baseY + h * 0.016f * z, cx + ch, coreTop, Comic.InkWidth(h * z), c.Blended(Comic.Ink));
            Gfx.Rect(cx - ch, baseY + h * 0.2f * z, cx + ch, baseY + h * 0.21f * z, c.Blended(Comic.Band));
            Gfx.Rect(cx - ch, coreTop - h * 0.012f * z, cx + ch, coreTop, c.Blended(Comic.Band));
            Comic.Capsule(c, new Pose(cx, coreTop, 0f), ch, h * z, 1f);
            GL.End();
        }

        public override void DrawGui(EffectContext c)
        {
            int n = _pairs * 2;
            if (n != _shown)
            {
                _shown = n;
                _count = "BOOSTERS  " + n;
            }
            c.Label(c.W * 0.93f, c.H * 0.92f, _count, new Color(0.92f, 0.9f, 0.85f, 0.8f), c.H * 0.026f, true);   // top right: the load problems notice takes the top left
        }
    }

    /// <summary>A small station in orbit; tentacles rise from below as the game loads, and the station starts to shake.</summary>
    internal sealed class KrakenEffect : LoadingEffect
    {
        private const int Arms = 5, Links = 30;
        private readonly float[] _suckX = new float[Arms * Links], _suckY = new float[Arms * Links], _suckR = new float[Arms * Links];
        private float _t0;

        public override string Name => "kraken";
        public override float Boil => 1.6f;
        public override Color Background => new Color(0.004f, 0.005f, 0.012f, 1f);

        public override void Init(EffectContext c, System.Random rng) => _t0 = c.T;

        private static float Grip(EffectContext c) => c.Exiting ? 1f : Mathf.Clamp01(c.Progress);

        private float Shake(EffectContext c) => Mathf.Max(0f, Noise.Value((c.T - _t0) * 1.3f, 4f) - 0.55f) * 2.2f * (0.4f + Grip(c));

        public override void Draw(EffectContext c)
        {
            float w = c.W, h = c.H, lt = c.T - _t0, p = Grip(c), top = h * 0.16f, shake = Shake(c);
            float sx = w * 0.6f + shake * h * 0.012f * Mathf.Sin(lt * 53f), sy = h * 0.66f + shake * h * 0.01f * Mathf.Sin(lt * 41f + 1f);
            float sa = 0.08f * Mathf.Sin(lt * 0.2f) + shake * 0.06f * Mathf.Sin(lt * 37f);
            if (c.Additive())
            {
                GL.Begin(GL.QUADS);
                SceneBits.Stars(c, 260, 73, top, 0.75f);
                GL.End();
            }
            if (c.Blend())
                Comic.Limb(c, top, c.Blended(new Color(0.02f, 0.035f, 0.07f)), c.Blended(new Color(0.06f, 0.12f, 0.22f)));
            if (c.Additive())
                Comic.LimbGlow(c, top, new Color(0.3f, 0.5f, 0.9f), 0.45f);
            if (!c.Blend())
                return;
            // Tentacles: each link bends a little more than the last, swaying, curling at the tip.
            int suckers = 0;
            GL.Begin(GL.QUADS);
            for (int a = 0; a < Arms; a++)
            {
                float x = w * (0.1f + 0.8f * a / (Arms - 1)), y = -h * 0.04f;
                float reach = h * (0.28f + 0.42f * p) * (0.75f + 0.25f * Noise.Hash(a, 3)), step = reach / Links;
                float aim = Mathf.Atan2(sy - y, sx - x), baseW = h * 0.045f * (0.8f + 0.3f * Noise.Hash(a, 8));
                for (int i = 0; i < Links; i++)
                {
                    float f = (float)i / Links;
                    float ang = aim + 0.55f * Mathf.Sin(lt * 0.8f + a * 1.9f - f * 5f) * f + f * f * f * 1.6f * Mathf.Sin(lt * 0.5f + a);
                    float cs = Mathf.Cos(ang), sn = Mathf.Sin(ang), nx = x + cs * step, ny = y + sn * step, width = baseW * (1f - f * 0.92f);
                    Gfx.Thick(x - cs * width * 0.2f, y - sn * width * 0.2f, nx, ny, width, c.Blended(Gfx.Lerp(new Color(0.22f, 0.09f, 0.28f), new Color(0.48f, 0.22f, 0.5f), f)));
                    if (i % 3 == 1)
                    {
                        _suckX[suckers] = x - sn * width * 0.28f;
                        _suckY[suckers] = y + cs * width * 0.28f;
                        _suckR[suckers] = width * 0.2f;
                        suckers++;
                    }
                    x = nx; y = ny;
                }
            }
            GL.End();
            for (int i = 0; i < suckers; i++)
                Gfx.Fan(_suckX[i], _suckY[i], _suckR[i], _suckR[i], 8, c.Blended(new Color(0.85f, 0.6f, 0.75f), 0.8f), c.Blended(new Color(0.6f, 0.35f, 0.55f), 0.8f));
            // The station: a long module, two solar wings, a pod docked at one end.
            var st = new Pose(sx, sy, sa);
            GL.Begin(GL.QUADS);
            Color truss = c.Blended(new Color(0.4f, 0.41f, 0.44f)), panel = c.Blended(new Color(0.12f, 0.2f, 0.42f)), grid = c.Blended(new Color(0.3f, 0.42f, 0.7f));
            for (int side = -1; side <= 1; side += 2)
            {
                st.Box(-h * 0.004f, side * h * 0.018f, h * 0.004f, side * h * 0.11f, truss);
                float p0 = side * h * 0.04f, p1 = side * h * 0.105f;
                st.Box(-h * 0.05f, Mathf.Min(p0, p1), h * 0.05f, Mathf.Max(p0, p1), panel);
                for (int g = -2; g <= 2; g++)
                    st.Box(g * h * 0.02f - 0.6f, Mathf.Min(p0, p1), g * h * 0.02f + 0.6f, Mathf.Max(p0, p1), grid);
            }
            st.Box(-h * 0.09f, -h * 0.018f, h * 0.09f, h * 0.018f, c.Blended(new Color(0.62f, 0.63f, 0.66f)));
            st.Box(-h * 0.09f, h * 0.006f, h * 0.09f, h * 0.012f, c.Blended(new Color(0.85f, 0.86f, 0.88f)));
            st.Box(-h * 0.09f, -h * 0.018f, h * 0.09f, -h * 0.01f, c.Blended(new Color(0.36f, 0.37f, 0.4f)));
            st.Box(-h * 0.03f, -h * 0.022f, -h * 0.024f, h * 0.022f, c.Blended(Comic.Band));
            st.Box(h * 0.024f, -h * 0.022f, h * 0.03f, h * 0.022f, c.Blended(Comic.Band));
            st.InkBox(-h * 0.09f, -h * 0.018f, h * 0.09f, h * 0.018f, Comic.InkWidth(h), c.Blended(Comic.Ink));
            for (int side = -1; side <= 1; side += 2)
                st.InkBox(-h * 0.05f, side < 0 ? -h * 0.105f : h * 0.04f, h * 0.05f, side < 0 ? -h * 0.04f : h * 0.105f, Comic.InkWidth(h), c.Blended(Comic.Ink));
            Comic.Capsule(c, new Pose(st.Px(h * 0.09f, 0f), st.Py(h * 0.09f, 0f), sa - Mathf.PI * 0.5f), h * 0.02f, h * 0.8f, 1f);
            GL.End();
        }

    }

    /// <summary>A Mun lander runs dry a little too high, bounces, tumbles and comes to rest on its side. Landed, technically.</summary>
    internal sealed class BounceEffect : LoadingEffect
    {
        private const float Gravity = 0.32f, Ground = 0.24f;      // in screen heights
        private const int Dust = 64;
        // Lander outline (feet, body corners, pod top, antenna) in screen heights, for the ground contact.
        private static readonly float[] HullX = { -0.062f, 0.062f, -0.032f, 0.032f, -0.012f, 0.012f, 0.02f };
        private static readonly float[] HullY = { -0.046f, -0.046f, 0.028f, 0.028f, 0.068f, 0.068f, 0.085f };
        private readonly float[] _dx = new float[Dust], _dy = new float[Dust], _dvx = new float[Dust], _dvy = new float[Dust], _dage = new float[Dust];
        private System.Random _rng;
        private float _x, _y, _vx, _vy, _ang, _spin, _rest, _life, _aspect = 1.78f;
        private int _state, _bounces, _nextDust;

        public override string Name => "bounce";
        public override float Boil => 1.6f;
        public override Color Background => new Color(0f, 0f, 0f, 1f);

        public override void Init(EffectContext c, System.Random rng)
        {
            _rng = rng;
            for (int i = 0; i < Dust; i++)
                _dage[i] = 99f;
            Reset(c);
        }

        private void Reset(EffectContext c)
        {
            _aspect = c.W / Mathf.Max(1f, c.H);
            _x = _aspect * 0.3f; _y = 1.08f; _vx = 0.07f; _vy = -0.12f;
            _ang = -0.15f; _spin = 0f; _rest = 0f; _life = 0f;
            _state = 0; _bounces = 0;
        }

        private float Lowest()
        {
            float cs = Mathf.Cos(_ang), sn = Mathf.Sin(_ang), low = float.MaxValue;
            for (int i = 0; i < HullX.Length; i++)
                low = Mathf.Min(low, _y + HullX[i] * sn + HullY[i] * cs);
            return low;
        }

        public override void Step(EffectContext c)
        {
            float dt = Mathf.Min(c.Dt, 0.1f);
            int steps = Mathf.Clamp(Mathf.CeilToInt(dt / 0.01f), 1, 10);
            for (int s = 0; s < steps; s++)
                Simulate(c, dt / steps);
            for (int i = 0; i < Dust; i++)
            {
                if (_dage[i] > 4f)
                    continue;
                _dage[i] += dt;
                _dx[i] += _dvx[i] * dt;
                _dy[i] += _dvy[i] * dt;
                _dvy[i] -= 0.04f * dt;
                _dvx[i] *= 1f - 0.8f * dt;
            }
        }

        private void Simulate(EffectContext c, float dt)
        {
            _life += dt;
            if (_life > 16f || _x > _aspect * 1.1f)
            {
                Reset(c);
                return;
            }
            if (_state == 3)
            {
                // At rest: settle on a side (never upright, that would be too easy), stay on the ground.
                float a = Mathf.Repeat(_ang + Mathf.PI, Gfx.Tau) - Mathf.PI, quarter = Mathf.PI * 0.5f;
                float target = Mathf.Round(a / quarter) * quarter;
                if (Mathf.Abs(target) < 0.01f)
                    target = a < 0f ? -quarter : quarter;
                _ang = Mathf.MoveTowards(a, target, dt * 1.5f);
                _y += Ground - Lowest();
                _rest += dt;
                if (_rest > 3.2f)
                    Reset(c);
                return;
            }
            float g = Gravity;
            if (_state == 0)
            {
                g -= 0.26f;
                _ang = Mathf.MoveTowards(_ang, 0f, 0.1f * dt);
                if (_y < 0.5f)
                    _state = 1;
            }
            _vy -= g * dt;
            _x += _vx * dt;
            _y += _vy * dt;
            _ang += _spin * dt;
            float low = Lowest();
            if (low >= Ground)
                return;
            _y += Ground - low;
            if (_vy < 0f)
            {
                float impact = -_vy;
                if (impact > 0.06f)
                {
                    _bounces++;
                    _state = 2;
                    Puff(impact);
                    _vy = impact * 0.52f;
                    _spin += ((float)_rng.NextDouble() - 0.5f) * 14f * impact + _vx * 6f;
                    _vx *= 0.75f;
                }
                else
                {
                    _state = 3;
                    _vy = _vx = _spin = 0f;
                }
            }
        }

        private void Puff(float impact)
        {
            for (int k = 0; k < 10; k++)
            {
                int i = _nextDust;
                _nextDust = (_nextDust + 1) % Dust;
                float a = Mathf.PI * (0.1f + 0.8f * (float)_rng.NextDouble()), sp = impact * (0.15f + 0.35f * (float)_rng.NextDouble());
                _dx[i] = _x + ((float)_rng.NextDouble() - 0.5f) * 0.06f;
                _dy[i] = Ground;
                _dvx[i] = Mathf.Cos(a) * sp;
                _dvy[i] = Mathf.Sin(a) * sp * 0.5f;
                _dage[i] = 0f;
            }
        }

        public override void Draw(EffectContext c)
        {
            float w = c.W, h = c.H, gy = Ground * h;
            if (c.Additive())
            {
                GL.Begin(GL.QUADS);
                SceneBits.Stars(c, 220, 11, h * 0.3f, 0.7f);
                GL.End();
            }
            if (!c.Blend())
                return;
            // Kerbin in the sky, half lit.
            float kx = w * 0.8f, ky = h * 0.78f, kr = h * 0.06f;
            Gfx.Fan(kx, ky, kr, kr, 40, c.Blended(new Color(0.22f, 0.44f, 0.76f)), c.Blended(new Color(0.22f, 0.44f, 0.76f)));
            Gfx.Fan(kx - kr * 0.25f, ky + kr * 0.2f, kr * 0.35f, kr * 0.22f, 16, c.Blended(new Color(0.3f, 0.52f, 0.24f)), c.Blended(new Color(0.3f, 0.52f, 0.24f)));
            Gfx.Fan(kx, ky, kr * 1.01f, kr * 1.01f, 40, c.Blended(Color.black, 0.75f), c.Blended(Color.black, 0.75f), Mathf.PI * 0.5f, Mathf.PI * 1.5f);
            GL.Begin(GL.QUADS);
            SceneBits.Ridge(c, gy + h * 0.08f, h * 0.12f, 0.0035f, 5.3f, c.Blended(new Color(0.2f, 0.2f, 0.22f)));
            Gfx.Bands(0f, 0f, w, gy, c.Blended(new Color(0.17f, 0.17f, 0.18f)), c.Blended(new Color(0.36f, 0.36f, 0.37f)), 4);
            GL.End();
            for (int i = 0; i < 6; i++)
            {
                float cx = w * Noise.Hash(i, 61), cy = gy * (0.15f + 0.7f * Noise.Hash(61, i)), r = h * (0.03f + 0.05f * Noise.Hash(i, 62));
                Gfx.Fan(cx, cy + r * 0.04f, r * 1.08f, r * 0.24f, 24, c.Blended(new Color(0.42f, 0.42f, 0.43f)), c.Blended(new Color(0.4f, 0.4f, 0.41f)));
                Gfx.Fan(cx, cy, r, r * 0.2f, 24, c.Blended(new Color(0.16f, 0.16f, 0.17f)), c.Blended(new Color(0.24f, 0.24f, 0.25f)));
            }
            for (int i = 0; i < Dust; i++)
            {
                if (_dage[i] > 3f)
                    continue;
                float f = _dage[i] / 3f, r = h * (0.01f + 0.03f * f);
                Comic.Puff(c, _dx[i] * h, _dy[i] * h, r * 0.8f, new Color(0.62f, 0.62f, 0.62f), 0.75f * (1f - f));
            }
            var p = new Pose(_x * h, _y * h, _ang);
            GL.Begin(GL.QUADS);
            Color leg = c.Blended(new Color(0.55f, 0.56f, 0.58f)), dark = c.Blended(new Color(0.12f, 0.12f, 0.13f));
            p.Thick(-0.025f * h, -0.01f * h, -0.055f * h, -0.042f * h, h * 0.005f, leg);
            p.Thick(0.025f * h, -0.01f * h, 0.055f * h, -0.042f * h, h * 0.005f, leg);
            p.Box(-0.064f * h, -0.046f * h, -0.046f * h, -0.04f * h, leg);
            p.Box(0.046f * h, -0.046f * h, 0.064f * h, -0.04f * h, leg);
            p.Trap(-0.03f * h, -0.015f * h, 0.016f * h, 0.008f * h, dark, c.Blended(new Color(0.3f, 0.3f, 0.32f)));
            p.BoxH(-0.032f * h, -0.015f * h, 0.032f * h, 0.028f * h, c.Blended(new Color(0.5f, 0.38f, 0.16f)), c.Blended(new Color(0.9f, 0.76f, 0.42f)));
            p.Trap(0.028f * h, 0.068f * h, 0.026f * h, 0.012f * h, c.Blended(new Color(0.26f, 0.27f, 0.29f)), c.Blended(new Color(0.64f, 0.65f, 0.68f)));
            p.Box(-0.006f * h, 0.04f * h, 0.008f * h, 0.052f * h, c.Blended(new Color(0.07f, 0.1f, 0.16f)));
            p.Thick(0.01f * h, 0.066f * h, 0.02f * h, 0.085f * h, Mathf.Max(1.2f, h * 0.002f), leg);
            Color ink = c.Blended(Comic.Ink);
            p.InkBox(-0.032f * h, -0.015f * h, 0.032f * h, 0.028f * h, Comic.InkWidth(h), ink);
            p.InkTrap(0.028f * h, 0.068f * h, 0.026f * h, 0.012f * h, Comic.InkWidth(h), ink);
            GL.End();
            if (_state == 0)
            {
                float fl = 0.85f + 0.15f * Noise.Value(c.T * 15f, 2f);
                MemeBits.Flame(c, p.Px(0f, -0.03f * h), p.Py(0f, -0.03f * h), -Mathf.PI * 0.5f + _ang, h * 0.06f * fl, 0.22f, 1f);
            }
        }

    }
}
