using Hyperdrive.Core;
using UnityEngine;

namespace Hyperdrive.Rendering
{
    /// <summary>Shared bits for the landscape scenes: a fixed star field.</summary>
    internal static class SceneBits
    {
        // Inside GL.QUADS (additive): n stars above minY, positions from the noise hash so every scene has its own sky.
        public static void Stars(EffectContext c, int n, int seed, float minY, float brightness)
        {
            for (int i = 0; i < n; i++)
            {
                float x = Noise.Hash(i, seed) * c.W, y = minY + Noise.Hash(seed, i) * (c.H - minY);
                float b = 0.15f + 0.85f * Noise.Hash(i * 7, seed + 3);
                b *= b * brightness * (0.85f + 0.15f * Mathf.Sin(c.T * (1.3f + b) + i));
                float s = 0.8f + 1.2f * Noise.Hash(i, seed + 11);
                Gfx.Rect(x - s, y - s, x + s, y + s, c.Glow(Color.white, b));
            }
        }

        // Inside GL.QUADS (blend): a ridge silhouette filled down to the bottom of the screen.
        public static void Ridge(EffectContext c, float baseY, float amp, float freq, float seed, Color col, int steps = 96)
        {
            float px = 0f, py = 0f;
            for (int i = 0; i <= steps; i++)
            {
                float x = c.W * i / steps;
                float n = Noise.Fbm(x * freq + seed, seed * 0.37f, 4);
                float y = baseY + amp * (n - 0.35f);
                if (i > 0)
                    Gfx.Quad(px, 0f, px, py, x, y, x, 0f, col);
                px = x; py = y;
            }
        }
    }

    /// <summary>Night at the pad: floodlights on the rocket, the tower's lattice, venting steam, countdown.</summary>
    internal sealed class LaunchPadEffect : LoadingEffect
    {
        private const int Puffs = 150;
        private readonly float[] _x = new float[Puffs], _y = new float[Puffs], _vx = new float[Puffs], _vy = new float[Puffs], _age = new float[Puffs], _life = new float[Puffs];
        private System.Random _rng;
        private int _shownSeconds = -1;
        private string _count = "";

        public override string Name => "launchpad";
        public override Color Background => new Color(0.01f, 0.014f, 0.03f, 1f);

        public override void Init(EffectContext c, System.Random rng)
        {
            _rng = rng;
            for (int i = 0; i < Puffs; i++)
            {
                Spawn(i);
                _age[i] = Rand(rng) * _life[i];
            }
        }

        private void Spawn(int i)
        {
            float side = Rand(_rng) < 0.5f ? -1f : 1f;
            _x[i] = 0.55f + side * Rand(_rng, 0.0f, 0.03f);
            _y[i] = 0.205f;
            _vx[i] = side * Rand(_rng, 0.01f, 0.05f);
            _vy[i] = Rand(_rng, 0.004f, 0.02f);
            _age[i] = 0f;
            _life[i] = Rand(_rng, 3f, 7f);
        }

        public override void Step(EffectContext c)
        {
            float dt = c.Dt;
            for (int i = 0; i < Puffs; i++)
            {
                _age[i] += dt;
                if (_age[i] > _life[i])
                    Spawn(i);
                _x[i] += _vx[i] * dt;
                _y[i] += _vy[i] * dt;
                _vx[i] *= 1f - 0.5f * dt;
            }
        }

        public override void Draw(EffectContext c)
        {
            float w = c.W, h = c.H, ground = h * 0.2f, rx = w * 0.55f, rr = h * 0.022f, top = ground + h * 0.52f;
            if (c.Blend())
            {
                GL.Begin(GL.QUADS);
                Gfx.RectV(0f, ground, w, h, c.Blended(new Color(0.06f, 0.07f, 0.1f)), c.Blended(new Color(0.01f, 0.014f, 0.03f)));
                Gfx.RectV(0f, 0f, w, ground, c.Blended(new Color(0.01f, 0.011f, 0.014f)), c.Blended(new Color(0.03f, 0.032f, 0.036f)));
                GL.End();
            }
            if (c.Additive())
            {
                GL.Begin(GL.QUADS);
                SceneBits.Stars(c, 140, 31, h * 0.45f, 0.6f);
                GL.End();
                // Floodlights from the ground onto the rocket.
                for (int i = 0; i < 3; i++)
                {
                    float lx = w * (0.35f + 0.2f * i), ly = ground;
                    float a = Mathf.Atan2(top * 0.7f - ly, rx - lx);
                    Gfx.Fan(lx, ly, h * 0.75f, h * 0.75f, 6, c.Glow(new Color(0.8f, 0.85f, 1f), 0.09f), Color.clear, a - 0.06f, a + 0.06f);
                }
            }
            if (!c.Blend())
                return;
            GL.Begin(GL.QUADS);
            // Tower: two rails and cross bracing, left of the rocket.
            float tx = rx - rr * 4.2f, tw = rr * 2.2f;
            Color steel = c.Blended(new Color(0.13f, 0.14f, 0.16f));
            Gfx.Rect(tx, ground, tx + 3f, top + h * 0.06f, steel);
            Gfx.Rect(tx + tw, ground, tx + tw + 3f, top + h * 0.06f, steel);
            for (float y = ground; y < top + h * 0.05f; y += tw)
            {
                Gfx.Thick(tx, y, tx + tw, y + tw, 1.6f, steel);
                Gfx.Thick(tx + tw, y, tx, y + tw, 1.6f, steel);
            }
            Gfx.Rect(tx + tw, top - h * 0.12f, rx - rr, top - h * 0.115f, steel);
            Gfx.Rect(tx + tw, ground + h * 0.18f, rx - rr, ground + h * 0.185f, steel);
            // Rocket: lit from the floodlights on the right, two dark interstage bands.
            Color lit = c.Blended(new Color(0.86f, 0.87f, 0.9f)), shade = c.Blended(new Color(0.3f, 0.32f, 0.36f));
            Gfx.RectH(rx - rr, ground + h * 0.02f, rx + rr, top, shade, lit);
            Color band = c.Blended(new Color(0.08f, 0.08f, 0.09f));
            Gfx.Rect(rx - rr, ground + h * 0.21f, rx + rr, ground + h * 0.225f, band);
            Gfx.Rect(rx - rr, ground + h * 0.38f, rx + rr, ground + h * 0.39f, band);
            Gfx.Quad(rx - rr, top, rx, top + rr * 3.2f, rx, top + rr * 3.2f, rx + rr, top, lit);
            Gfx.Quad(rx - rr, ground + h * 0.02f, rx - rr * 1.9f, ground, rx - rr, ground + h * 0.09f, rx - rr, ground + h * 0.09f, shade);
            Gfx.Quad(rx + rr, ground + h * 0.02f, rx + rr * 1.9f, ground, rx + rr, ground + h * 0.09f, rx + rr, ground + h * 0.09f, lit);
            Gfx.Rect(rx - rr * 2.6f, ground - h * 0.01f, rx + rr * 2.6f, ground + h * 0.004f, steel);
            GL.End();
            // Steam, soft, drifting off the pad.
            for (int i = 0; i < Puffs; i++)
            {
                float f = _age[i] / _life[i];
                float r = h * (0.015f + 0.06f * f);
                float a = 0.10f * (1f - f) * Mathf.Min(1f, f * 8f);
                Gfx.Fan(_x[i] * w, _y[i] * h + r * 0.4f, r, r * 0.8f, 14, c.Blended(new Color(0.85f, 0.87f, 0.92f), a), c.Blended(new Color(0.85f, 0.87f, 0.92f), 0f));
            }
            if (c.Additive() && Mathf.Repeat(c.T, 1.6f) < 0.25f)
                Gfx.Fan(tx + tw * 0.5f, top + h * 0.065f, h * 0.012f, h * 0.012f, 12, c.Glow(new Color(1f, 0.2f, 0.15f)), Color.clear);
        }

        public override void DrawGui(EffectContext c)
        {
            int seconds = c.Exiting ? 0 : Mathf.CeilToInt((1f - c.Progress) * 600f);
            if (seconds != _shownSeconds)
            {
                _shownSeconds = seconds;
                _count = "T- " + (seconds / 60).ToString("00") + ":" + (seconds % 60).ToString("00");
            }
            float size = c.H * 0.03f;
            c.Label(c.W * 0.93f, c.H * 0.92f, _count, new Color(1f, 0.85f, 0.55f, 0.85f), size, true);
            c.Label(c.W * 0.93f, c.H * 0.92f - size * 1.4f, c.Tr("#HD_PadLabel"), new Color(0.8f, 0.85f, 0.95f, 0.5f), size * 0.5f, true);
        }
    }

    /// <summary>Powered descent over the Mun: ridgelines rush in below, Kerbin hangs in the black sky.</summary>
    internal sealed class MunDescentEffect : LoadingEffect
    {
        private const int Rows = 34, Cols = 80;
        private int _shownAlt = -1;
        private string _alt = "";

        public override string Name => "mun";
        public override Color Background => new Color(0f, 0f, 0f, 1f);

        private static float Altitude(EffectContext c) => c.Exiting ? 0f : Mathf.Lerp(4800f, 40f, Mathf.Pow(c.Progress, 0.8f));

        public override void Draw(EffectContext c)
        {
            float w = c.W, h = c.H, horizon = h * 0.62f, f = h * 0.9f;
            if (c.Additive())
            {
                GL.Begin(GL.QUADS);
                SceneBits.Stars(c, 160, 47, horizon, 0.5f);
                GL.End();
                Gfx.Ring(w * 0.78f, h * 0.82f, h * 0.05f, h * 0.06f, 64, c.Glow(new Color(0.4f, 0.65f, 1f), 0.35f), Color.clear);
            }
            if (!c.Blend())
                return;
            float kx = w * 0.78f, ky = h * 0.82f, kr = h * 0.05f;
            Gfx.Fan(kx, ky, kr, kr, 48, c.Blended(new Color(0.12f, 0.24f, 0.45f)), c.Blended(new Color(0.06f, 0.1f, 0.2f)));
            Gfx.Fan(kx + kr * 0.35f, ky + kr * 0.2f, kr * 0.72f, kr * 0.72f, 32, c.Blended(new Color(0.45f, 0.65f, 0.95f), 0.85f), c.Blended(new Color(0.3f, 0.5f, 0.85f), 0f));

            float cam = 1.1f + 2.2f * Altitude(c) / 4800f;     // camera height above the mean terrain
            float scroll = c.T * 0.9f;
            float near = 0.9f, far = 22f;
            Color fill = c.Blended(new Color(0f, 0f, 0f));
            for (int r = Rows - 1; r >= 0; r--)
            {
                float u = (r + 1f - Mathf.Repeat(scroll, 1f)) / Rows;
                float z = near + (far - near) * u * u;
                float zWorld = z + scroll;
                float shade = Mathf.Clamp01(1.15f - u * 1.05f);
                Color line = c.Blended(new Color(0.78f, 0.78f, 0.76f), 0.85f * shade);
                float px = 0f, py = 0f;
                GL.Begin(GL.QUADS);
                for (int i = 0; i <= Cols; i++)
                {
                    float xw = (i / (float)Cols - 0.5f) * z * 2.4f;
                    float n = Noise.Fbm(xw * 0.35f + 11f, zWorld * 0.35f, 4);
                    float ridge = 1f - Mathf.Abs(2f * n - 1f);
                    float height = ridge * ridge * 1.6f;
                    float x = w * 0.5f + xw / z * f, y = horizon - (cam - height) / z * f;
                    if (i > 0)
                        Gfx.Quad(px, -10f, px, py, x, y, x, -10f, fill);
                    px = x; py = y;
                }
                GL.End();
                GL.Begin(GL.LINES);
                for (int i = 0; i <= Cols; i++)
                {
                    float xw = (i / (float)Cols - 0.5f) * z * 2.4f;
                    float n = Noise.Fbm(xw * 0.35f + 11f, zWorld * 0.35f, 4);
                    float ridge = 1f - Mathf.Abs(2f * n - 1f);
                    float height = ridge * ridge * 1.6f;
                    float x = w * 0.5f + xw / z * f, y = horizon - (cam - height) / z * f;
                    if (i > 0)
                        Gfx.Line(px, py, x, y, line);
                    px = x; py = y;
                }
                GL.End();
            }
        }

        public override void DrawGui(EffectContext c)
        {
            int alt = Mathf.RoundToInt(Altitude(c));
            if (alt != _shownAlt)
            {
                _shownAlt = alt;
                _alt = Lang.F("#HD_Alt", alt);
            }
            float size = c.H * 0.022f;
            c.Label(c.W * 0.93f, c.H * 0.5f, _alt, new Color(0.9f, 0.9f, 0.85f, 0.8f), size, true);
            c.Label(c.W * 0.93f, c.H * 0.5f - size * 1.5f, c.Tr("#HD_VSpeed"), new Color(0.9f, 0.9f, 0.85f, 0.5f), size * 0.7f, true);
        }
    }

    /// <summary>Climbing through the cloud deck into space: the sky darkens and the stars come out as the game loads.</summary>
    internal sealed class AscentEffect : LoadingEffect
    {
        private const int Clouds = 70;
        private readonly float[] _x = new float[Clouds], _y = new float[Clouds], _d = new float[Clouds], _s = new float[Clouds];
        private System.Random _rng;
        private int _shownAlt = -1;
        private string _alt = "";

        public override string Name => "ascent";

        public override void Init(EffectContext c, System.Random rng)
        {
            _rng = rng;
            for (int i = 0; i < Clouds; i++)
                Spawn(i, Rand(rng, -0.2f, 1.2f));
        }

        private void Spawn(int i, float y)
        {
            _x[i] = Rand(_rng, -0.15f, 1.15f);
            _y[i] = y;
            _d[i] = Rand(_rng, 0.3f, 1f);
            _s[i] = Rand(_rng, 0.6f, 1.4f);
        }

        public override void Step(EffectContext c)
        {
            float speed = 0.22f * (1f - 0.75f * c.Progress) * (0.3f + 0.7f * c.Energy + 0.2f);
            for (int i = 0; i < Clouds; i++)
            {
                _y[i] -= speed * (0.4f + _d[i]) * c.Dt;
                if (_y[i] < -0.3f)
                    Spawn(i, 1.3f);
            }
        }

        public override void Draw(EffectContext c)
        {
            float w = c.W, h = c.H, p = c.Exiting ? 1f : c.Progress;
            if (c.Blend())
            {
                GL.Begin(GL.QUADS);
                Color low = Gfx.Lerp(new Color(0.52f, 0.68f, 0.88f), new Color(0.02f, 0.03f, 0.07f), p * 1.1f);
                Color high = Gfx.Lerp(new Color(0.24f, 0.44f, 0.78f), new Color(0f, 0f, 0.01f), p * 1.25f);
                Gfx.RectV(0f, 0f, w, h, c.Blended(low), c.Blended(high));
                GL.End();
            }
            float stars = Mathf.SmoothStep(0f, 1f, (p - 0.5f) / 0.4f);
            if (stars > 0f && c.Additive())
            {
                GL.Begin(GL.QUADS);
                SceneBits.Stars(c, 220, 53, 0f, stars);
                GL.End();
            }
            if (p > 0.7f && c.Blend())
            {
                // Kerbin's limb rising into view below.
                float k = (p - 0.7f) / 0.3f, R = h * 2.6f, cy = -R + h * 0.16f * k;
                Gfx.Fan(w * 0.5f, cy, R, R, 96, c.Blended(new Color(0.08f, 0.16f, 0.32f)), c.Blended(new Color(0.12f, 0.3f, 0.6f)), 1.2f, 1.94f);
                if (c.Additive())
                    Gfx.Ring(w * 0.5f, cy, R, R + h * 0.04f, 96, c.Glow(new Color(0.35f, 0.62f, 1f), 0.7f * k), Color.clear, 1.2f, 1.94f);
            }
            float density = Mathf.Pow(1f - p, 0.8f);
            if (density <= 0.01f || !c.Blend())
                return;
            for (int i = 0; i < Clouds; i++)
            {
                float x = _x[i] * w, y = _y[i] * h, r = h * 0.09f * _s[i] * (0.5f + _d[i]);
                float a = 0.55f * density * (0.4f + 0.6f * _d[i]);
                Gfx.Fan(x, y - r * 0.15f, r * 1.4f, r * 0.5f, 28, c.Blended(new Color(0.74f, 0.78f, 0.86f), a), c.Blended(new Color(0.74f, 0.78f, 0.86f), a * 0.8f));
                Gfx.Fan(x - r * 0.45f, y + r * 0.1f, r * 0.72f, r * 0.58f, 28, c.Blended(new Color(0.95f, 0.96f, 0.99f), a), c.Blended(new Color(0.95f, 0.96f, 0.99f), a * 0.85f));
                Gfx.Fan(x + r * 0.3f, y + r * 0.22f, r * 0.62f, r * 0.55f, 28, c.Blended(new Color(1f, 1f, 1f), a), c.Blended(new Color(1f, 1f, 1f), a * 0.85f));
                GL.Begin(GL.QUADS);
                Gfx.Rect(x - r * 1.25f, y - r * 0.32f, x + r * 1.25f, y - r * 0.12f, c.Blended(new Color(0.66f, 0.7f, 0.8f), a * 0.9f));
                GL.End();
            }
        }

        public override void DrawGui(EffectContext c)
        {
            float p = c.Exiting ? 1f : c.Progress;
            int tenths = Mathf.RoundToInt(Mathf.Pow(p, 1.4f) * 720f);
            if (tenths != _shownAlt)
            {
                _shownAlt = tenths;
                _alt = Lang.F("#HD_AltKm", (tenths / 10) + (Lang.DecimalComma ? "," : ".") + (tenths % 10));
            }
            c.Label(c.W * 0.93f, c.H * 0.92f, _alt, new Color(1f, 1f, 1f, 0.8f), c.H * 0.026f, true);
        }
    }

    /// <summary>The Space Center at night: VAB and tower silhouettes, runway lights with a running strobe.</summary>
    internal sealed class KscNightEffect : LoadingEffect
    {
        public override string Name => "ksc";
        public override Color Background => new Color(0.01f, 0.016f, 0.035f, 1f);

        public override void Draw(EffectContext c)
        {
            float w = c.W, h = c.H, hz = h * 0.32f;
            if (c.Blend())
            {
                GL.Begin(GL.QUADS);
                Gfx.RectV(0f, hz, w, h, c.Blended(new Color(0.1f, 0.13f, 0.2f)), c.Blended(new Color(0.012f, 0.018f, 0.04f)));
                Gfx.RectV(0f, 0f, w * 0.34f, hz, c.Blended(new Color(0.01f, 0.018f, 0.03f)), c.Blended(new Color(0.03f, 0.05f, 0.08f)));
                Gfx.Rect(w * 0.34f, 0f, w, hz, c.Blended(new Color(0.014f, 0.016f, 0.018f)));
                GL.End();
            }
            if (c.Additive())
            {
                GL.Begin(GL.QUADS);
                SceneBits.Stars(c, 180, 61, hz + h * 0.1f, 0.6f);
                // The Mun's reflection on the bay.
                for (int i = 0; i < 26; i++)
                {
                    float y = hz - h * 0.01f - i * h * 0.009f, half = h * (0.012f + 0.0015f * i) * (0.6f + 0.4f * Noise.Value(i * 3f, c.T * 2f));
                    Gfx.Rect(w * 0.17f - half, y, w * 0.17f + half, y + 1.5f, c.Glow(new Color(0.8f, 0.82f, 0.9f), 0.25f * (1f - i / 26f)));
                }
                GL.End();
                Gfx.Fan(w * 0.17f, h * 0.8f, h * 0.02f, h * 0.02f, 32, c.Glow(new Color(0.85f, 0.86f, 0.9f), 0.8f), c.Glow(new Color(0.85f, 0.86f, 0.9f), 0.6f));
            }
            if (!c.Blend())
                return;
            Color sil = c.Blended(new Color(0.022f, 0.025f, 0.032f));
            GL.Begin(GL.QUADS);
            Gfx.Rect(w * 0.56f, hz, w * 0.70f, h * 0.62f, sil);                       // VAB
            Gfx.Rect(w * 0.70f, hz, w * 0.76f, h * 0.45f, sil);                       // VAB annex
            Gfx.Rect(w * 0.47f, hz, w * 0.53f, h * 0.38f, sil);                       // admin
            Gfx.Rect(w * 0.905f, hz, w * 0.912f, h * 0.70f, sil);                     // tower
            Gfx.Rect(w * 0.93f, hz, w * 0.937f, h * 0.70f, sil);
            for (float y = hz; y < h * 0.70f; y += h * 0.02f)
                Gfx.Thick(w * 0.905f, y, w * 0.937f, y + h * 0.02f, 1.5f, sil);
            Gfx.Rect(w * 0.84f, hz, w * 0.846f, h * 0.42f, sil);                       // tracking dish mast
            GL.End();
            Gfx.Fan(w * 0.843f, h * 0.44f, h * 0.05f, h * 0.025f, 32, sil, sil, Mathf.PI, Gfx.Tau);
            Gfx.Fan(w * 0.38f, hz, w * 0.06f, h * 0.075f, 32, sil, sil, 0f, Mathf.PI);   // SPH hangar

            if (!c.Additive())
                return;
            GL.Begin(GL.QUADS);
            // VAB door light, faint and warm.
            Gfx.RectV(w * 0.61f, hz, w * 0.65f, h * 0.55f, c.Glow(new Color(1f, 0.75f, 0.45f), 0.12f), c.Glow(new Color(1f, 0.75f, 0.45f), 0.02f));
            // Runway: two rows of lights converging on the horizon, a strobe running away from us.
            const int N = 30;
            int strobe = (int)(c.T * 22f) % N;
            for (int side = 0; side < 2; side++)
            {
                float x0 = side == 0 ? w * 0.42f : w * 0.96f, vx = w * 0.62f;
                for (int i = 0; i < N; i++)
                {
                    float t = i / (float)N, d = t * t;
                    float x = Mathf.Lerp(x0, vx, d), y = Mathf.Lerp(0f, hz - 2f, d);
                    float s = Mathf.Lerp(4f, 0.8f, d);
                    Color col = i == strobe ? new Color(1f, 1f, 1f) : side == 0 ? new Color(1f, 0.78f, 0.45f) : new Color(1f, 0.78f, 0.45f);
                    Gfx.Rect(x - s, y - s, x + s, y + s, c.Glow(col, i == strobe ? 1f : 0.55f));
                }
            }
            GL.End();
            bool on = Mathf.Repeat(c.T, 1.4f) < 0.5f;
            if (on)
            {
                Gfx.Fan(w * 0.56f, h * 0.62f, h * 0.008f, h * 0.008f, 10, c.Glow(new Color(1f, 0.2f, 0.15f)), Color.clear);
                Gfx.Fan(w * 0.70f, h * 0.62f, h * 0.008f, h * 0.008f, 10, c.Glow(new Color(1f, 0.2f, 0.15f)), Color.clear);
                Gfx.Fan(w * 0.921f, h * 0.705f, h * 0.008f, h * 0.008f, 10, c.Glow(new Color(1f, 0.2f, 0.15f)), Color.clear);
            }
        }
    }

    /// <summary>Aurora over Kerbin's north pole: curtains of light above dark mountains.</summary>
    internal sealed class AuroraEffect : LoadingEffect
    {
        public override string Name => "aurora";
        public override Color Background => new Color(0.004f, 0.01f, 0.02f, 1f);

        public override void Draw(EffectContext c)
        {
            float w = c.W, h = c.H;
            if (c.Blend())
            {
                GL.Begin(GL.QUADS);
                Gfx.RectV(0f, 0f, w, h, c.Blended(new Color(0.02f, 0.05f, 0.08f)), c.Blended(new Color(0.003f, 0.008f, 0.02f)));
                GL.End();
            }
            if (c.Additive())
            {
                GL.Begin(GL.QUADS);
                SceneBits.Stars(c, 200, 71, h * 0.25f, 0.55f);
                float strength = 0.5f + 0.5f * c.Energy;
                for (int k = 0; k < 3; k++)
                {
                    const int N = 160;
                    float px = 0f, pb = 0f, ptop = 0f, pg = 0f;
                    for (int i = 0; i <= N; i++)
                    {
                        float x = w * (i / (float)N * 1.2f - 0.1f);
                        float baseY = h * (0.44f + 0.05f * k + 0.06f * Mathf.Sin(x * 0.0025f + c.T * 0.11f + k * 1.7f) + 0.04f * (Noise.Value(x * 0.004f, k * 5f + c.T * 0.05f) - 0.5f));
                        float top = baseY + h * (0.12f + 0.22f * Noise.Value(x * 0.006f + c.T * 0.12f, k * 3f));
                        float glow = Noise.Value(x * 0.012f - c.T * 0.35f, k * 9f);
                        glow = glow * glow * strength * (k == 1 ? 1f : 0.65f);
                        if (i > 0)
                        {
                            Color b0 = c.Glow(new Color(0.3f, 1f, 0.55f), pg * 0.55f), b1 = c.Glow(new Color(0.3f, 1f, 0.55f), glow * 0.55f);
                            GL.Color(b0); GL.Vertex3(px, pb, 0f);
                            GL.Color(c.Glow(new Color(0.55f, 0.28f, 0.9f), 0f)); GL.Vertex3(px, ptop, 0f);
                            GL.Vertex3(x, top, 0f);
                            GL.Color(b1); GL.Vertex3(x, baseY, 0f);
                            // The bright lower hem.
                            GL.Color(c.Glow(new Color(0.6f, 1f, 0.75f), pg * 0.8f)); GL.Vertex3(px, pb - 2f, 0f);
                            GL.Vertex3(px, pb + 2f, 0f);
                            GL.Color(c.Glow(new Color(0.6f, 1f, 0.75f), glow * 0.8f)); GL.Vertex3(x, baseY + 2f, 0f);
                            GL.Vertex3(x, baseY - 2f, 0f);
                        }
                        px = x; pb = baseY; ptop = top; pg = glow;
                    }
                }
                GL.End();
            }
            if (!c.Blend())
                return;
            GL.Begin(GL.QUADS);
            SceneBits.Ridge(c, h * 0.26f, h * 0.22f, 0.0035f, 3.1f, c.Blended(new Color(0.015f, 0.025f, 0.035f)));
            SceneBits.Ridge(c, h * 0.16f, h * 0.18f, 0.005f, 8.4f, c.Blended(new Color(0.004f, 0.008f, 0.012f)));
            GL.End();
        }
    }

    /// <summary>A polar outpost under falling snow: layered hills, a lit dome, a blinking mast.</summary>
    internal sealed class PolarEffect : LoadingEffect
    {
        private const int Flakes = 600;
        private readonly float[] _x = new float[Flakes], _y = new float[Flakes], _layer = new float[Flakes], _phase = new float[Flakes];

        public override string Name => "polar";
        public override Color Background => new Color(0.09f, 0.1f, 0.13f, 1f);

        public override void Init(EffectContext c, System.Random rng)
        {
            for (int i = 0; i < Flakes; i++)
            {
                _x[i] = Rand(rng); _y[i] = Rand(rng); _layer[i] = Rand(rng); _phase[i] = Rand(rng, 0f, 10f);
            }
        }

        public override void Step(EffectContext c)
        {
            for (int i = 0; i < Flakes; i++)
            {
                float l = _layer[i];
                _y[i] -= (0.025f + 0.09f * l) * c.Dt;
                _x[i] += (0.012f + 0.02f * Mathf.Sin(c.T * 0.7f + _phase[i])) * l * c.Dt;
                if (_y[i] < -0.02f) { _y[i] += 1.04f; }
                if (_x[i] > 1.02f) _x[i] -= 1.04f;
            }
        }

        private void Snow(EffectContext c, float minLayer, float maxLayer)
        {
            GL.Begin(GL.QUADS);
            for (int i = 0; i < Flakes; i++)
            {
                float l = _layer[i];
                if (l < minLayer || l >= maxLayer)
                    continue;
                float s = 0.8f + 3.2f * l * l;
                Gfx.Rect(_x[i] * c.W - s, _y[i] * c.H - s, _x[i] * c.W + s, _y[i] * c.H + s, c.Blended(new Color(0.92f, 0.94f, 0.98f), 0.35f + 0.5f * l));
            }
            GL.End();
        }

        public override void Draw(EffectContext c)
        {
            float w = c.W, h = c.H;
            if (!c.Blend())
                return;
            GL.Begin(GL.QUADS);
            Gfx.RectV(0f, 0f, w, h, c.Blended(new Color(0.2f, 0.22f, 0.26f)), c.Blended(new Color(0.07f, 0.08f, 0.11f)));
            SceneBits.Ridge(c, h * 0.36f, h * 0.14f, 0.002f, 1.7f, c.Blended(new Color(0.15f, 0.165f, 0.2f)));
            GL.End();
            Snow(c, 0f, 0.4f);
            GL.Begin(GL.QUADS);
            SceneBits.Ridge(c, h * 0.26f, h * 0.12f, 0.003f, 5.2f, c.Blended(new Color(0.21f, 0.23f, 0.28f)));
            GL.End();
            // Outpost on the middle ridge: a dome, its windows, the mast.
            float dx = w * 0.64f, dy = h * 0.27f, dr = h * 0.05f;
            Gfx.Fan(dx, dy, dr, dr * 0.8f, 32, c.Blended(new Color(0.5f, 0.52f, 0.56f)), c.Blended(new Color(0.3f, 0.32f, 0.36f)), 0f, Mathf.PI);
            GL.Begin(GL.QUADS);
            Gfx.Rect(dx + dr * 1.3f, dy, dx + dr * 1.36f, dy + dr * 2.4f, c.Blended(new Color(0.25f, 0.27f, 0.3f)));
            GL.End();
            Snow(c, 0.4f, 0.75f);
            GL.Begin(GL.QUADS);
            SceneBits.Ridge(c, h * 0.12f, h * 0.1f, 0.0045f, 9.3f, c.Blended(new Color(0.33f, 0.35f, 0.41f)));
            GL.End();
            if (c.Additive())
            {
                GL.Begin(GL.QUADS);
                for (int i = 0; i < 4; i++)
                    Gfx.Rect(dx - dr * 0.6f + i * dr * 0.34f, dy + dr * 0.15f, dx - dr * 0.6f + i * dr * 0.34f + dr * 0.16f, dy + dr * 0.3f, c.Glow(new Color(1f, 0.78f, 0.45f), 0.8f));
                GL.End();
                Gfx.Fan(dx, dy + dr * 0.3f, dr * 2.2f, dr * 1.2f, 24, c.Glow(new Color(1f, 0.75f, 0.45f), 0.08f), Color.clear);
                if (Mathf.Repeat(c.T, 2f) < 0.3f)
                    Gfx.Fan(dx + dr * 1.33f, dy + dr * 2.45f, h * 0.008f, h * 0.008f, 10, c.Glow(new Color(1f, 0.25f, 0.2f)), Color.clear);
            }
            if (c.Blend())
                Snow(c, 0.75f, 1.01f);
        }
    }

    /// <summary>Splashdown at dusk: a capsule under three parachutes settles towards a glittering sea.</summary>
    internal sealed class SplashdownEffect : LoadingEffect
    {
        public override string Name => "splashdown";

        public override void Draw(EffectContext c)
        {
            float w = c.W, h = c.H, hz = h * 0.36f, p = c.Exiting ? 1f : c.Progress;
            float sunX = w * 0.68f, sunY = hz + h * (0.06f - 0.07f * p), sunR = h * 0.045f;
            if (c.Blend())
            {
                GL.Begin(GL.QUADS);
                Gfx.RectV(0f, hz, w, hz + h * 0.12f, c.Blended(new Color(0.98f, 0.6f, 0.34f)), c.Blended(new Color(0.72f, 0.36f, 0.32f)));
                Gfx.RectV(0f, hz + h * 0.12f, w, hz + h * 0.32f, c.Blended(new Color(0.72f, 0.36f, 0.32f)), c.Blended(new Color(0.22f, 0.15f, 0.28f)));
                Gfx.RectV(0f, hz + h * 0.32f, w, h, c.Blended(new Color(0.22f, 0.15f, 0.28f)), c.Blended(new Color(0.04f, 0.05f, 0.12f)));
                GL.End();
                Gfx.Fan(sunX, sunY, sunR, sunR, 48, c.Blended(new Color(1f, 0.86f, 0.6f)), c.Blended(new Color(1f, 0.7f, 0.4f)));
                GL.Begin(GL.QUADS);
                Gfx.RectV(0f, 0f, w, hz, c.Blended(new Color(0.03f, 0.035f, 0.07f)), c.Blended(new Color(0.16f, 0.12f, 0.17f)));
                GL.End();
            }
            if (c.Additive())
            {
                Gfx.Fan(sunX, sunY, h * 0.35f, h * 0.2f, 40, c.Glow(new Color(1f, 0.6f, 0.3f), 0.25f), Color.clear);
                GL.Begin(GL.QUADS);
                // Glitter under the sun: short horizontal flashes, wider towards us.
                for (int i = 0; i < 260; i++)
                {
                    float d = Noise.Hash(i, 5);
                    float y = hz - 2f - d * d * hz;
                    float spread = h * (0.02f + 0.35f * d * d);
                    float x = sunX + (Noise.Hash(i, 9) - 0.5f) * 2f * spread;
                    float flick = Noise.Value(i * 1.7f, c.T * 6f);
                    float len = h * (0.004f + 0.02f * d);
                    Gfx.Rect(x - len, y, x + len, y + 1.2f + d, c.Glow(new Color(1f, 0.75f, 0.45f), flick * flick * (0.9f - 0.5f * d)));
                }
                GL.End();
            }
            if (!c.Blend())
                return;
            // Capsule and chutes, swaying, coming down with the progress.
            float sway = Mathf.Sin(c.T * 0.6f) * 0.03f;
            float capX = w * 0.36f + Mathf.Sin(c.T * 0.6f) * h * 0.01f, capY = h * Mathf.Lerp(0.66f, 0.42f, p);
            float chuteY = capY + h * 0.22f;
            Color line = c.Blended(new Color(0.1f, 0.08f, 0.1f), 0.8f);
            GL.Begin(GL.LINES);
            for (int k = -1; k <= 1; k++)
            {
                float cx = capX + k * h * 0.075f + sway * h;
                Gfx.Line(capX, capY + h * 0.03f, cx - h * 0.04f, chuteY, line);
                Gfx.Line(capX, capY + h * 0.03f, cx + h * 0.04f, chuteY, line);
            }
            GL.End();
            for (int k = -1; k <= 1; k++)
            {
                float cx = capX + k * h * 0.075f + sway * h, cy = chuteY + (k == 0 ? h * 0.02f : 0f);
                for (int g = 0; g < 6; g++)
                {
                    float a0 = Mathf.PI * g / 6f, a1 = Mathf.PI * (g + 1) / 6f;
                    Color gore = g % 2 == 0 ? new Color(0.95f, 0.45f, 0.18f) : new Color(0.95f, 0.92f, 0.86f);
                    Gfx.Fan(cx, cy, h * 0.045f, h * 0.035f, 4, c.Blended(gore * 0.75f), c.Blended(gore), a0, a1);
                }
            }
            Color hull = c.Blended(new Color(0.12f, 0.11f, 0.13f));
            GL.Begin(GL.QUADS);
            float cs = h * 0.022f;
            Gfx.Quad(capX - cs, capY - cs * 0.6f, capX - cs * 0.45f, capY + cs * 1.2f, capX + cs * 0.45f, capY + cs * 1.2f, capX + cs, capY - cs * 0.6f, hull);
            Gfx.Rect(capX - cs * 1.05f, capY - cs * 0.8f, capX + cs * 1.05f, capY - cs * 0.6f, c.Blended(new Color(0.35f, 0.22f, 0.15f)));
            GL.End();
        }
    }

    /// <summary>Re-entry: the heat shield glows, plasma streams past the capsule, then it cools down.</summary>
    internal sealed class ReentryEffect : LoadingEffect
    {
        private const int Streaks = 220;
        private readonly float[] _u = new float[Streaks], _v = new float[Streaks], _age = new float[Streaks], _life = new float[Streaks];
        private System.Random _rng;

        public override string Name => "reentry";
        public override Color Background => new Color(0.006f, 0.008f, 0.018f, 1f);

        public override void Init(EffectContext c, System.Random rng)
        {
            _rng = rng;
            for (int i = 0; i < Streaks; i++)
            {
                Spawn(i);
                _age[i] = Rand(rng) * _life[i];
            }
        }

        private void Spawn(int i)
        {
            _u[i] = Rand(_rng, -1f, 1f);      // across the shield
            _v[i] = 0f;                        // along the flow, from the shield backwards
            _age[i] = 0f;
            _life[i] = Rand(_rng, 0.25f, 0.8f);
        }

        // Heating rises then fades: peak a little before the middle of the load.
        private static float Heat(EffectContext c) => c.Exiting ? 0f : Mathf.Clamp01(0.35f + Mathf.Sin(Mathf.Clamp01(c.Progress * 1.15f) * Mathf.PI) * 0.9f) * (0.4f + 0.6f * c.Energy);

        public override void Step(EffectContext c)
        {
            for (int i = 0; i < Streaks; i++)
            {
                _age[i] += c.Dt;
                _v[i] += c.Dt * (1.6f + Mathf.Abs(_u[i]));
                if (_age[i] > _life[i])
                    Spawn(i);
            }
        }

        public override void Draw(EffectContext c)
        {
            float w = c.W, h = c.H, cx = w * 0.5f, cy = h * 0.5f, s = h * 0.07f;
            float heat = Heat(c), flicker = 0.85f + 0.15f * Noise.Value(c.T * 9f, 1f);
            // Flow direction: from the lower left (shield) towards the upper right (behind the capsule).
            Vector2 flow = new Vector2(0.78f, 0.62f).normalized, across = new Vector2(-flow.y, flow.x);
            if (c.Additive())
            {
                GL.Begin(GL.QUADS);
                SceneBits.Stars(c, 120, 83, 0f, 0.35f * (1f - heat));
                GL.End();
                float shx = cx - flow.x * s * 0.9f, shy = cy - flow.y * s * 0.9f;
                Gfx.Fan(shx, shy, s * 3.2f, s * 3.2f, 48, c.Glow(new Color(1f, 0.45f, 0.2f), 0.55f * heat * flicker), Color.clear);
                Gfx.Fan(shx, shy, s * 1.6f, s * 1.6f, 40, c.Glow(new Color(1f, 0.85f, 0.6f), 0.8f * heat * flicker), Color.clear);
                GL.Begin(GL.QUADS);
                for (int i = 0; i < Streaks; i++)
                {
                    float f = _age[i] / _life[i];
                    float off = _u[i] * s * 1.1f;
                    float x0 = shx + across.x * off + flow.x * _v[i] * s, y0 = shy + across.y * off + flow.y * _v[i] * s;
                    float len = s * (0.6f + 1.4f * f);
                    Color col = Gfx.Lerp(new Color(1f, 0.85f, 0.55f), new Color(0.9f, 0.3f, 0.55f), f);
                    Gfx.Thick(x0, y0, x0 + flow.x * len, y0 + flow.y * len, 1.6f + 2f * (1f - f), c.Glow(col, 0.5f * heat * (1f - f)));
                }
                GL.End();
            }
            if (!c.Blend())
                return;
            // Capsule seen from the side, heat shield first.
            GL.Begin(GL.QUADS);
            Color hull = c.Blended(new Color(0.07f, 0.07f, 0.08f));
            Vector2 back = flow * s * 1.5f, front = -flow * s * 0.6f;
            Vector2 a = new Vector2(cx, cy) + front + across * s, b = new Vector2(cx, cy) + front - across * s;
            Vector2 d = new Vector2(cx, cy) + back + across * s * 0.35f, e = new Vector2(cx, cy) + back - across * s * 0.35f;
            Gfx.Quad(a.x, a.y, d.x, d.y, e.x, e.y, b.x, b.y, hull);
            Vector2 sa = a - flow * s * 0.12f, sb = b - flow * s * 0.12f;
            Gfx.Quad(sa.x, sa.y, a.x, a.y, b.x, b.y, sb.x, sb.y, c.Blended(Gfx.Lerp(new Color(0.2f, 0.12f, 0.08f), new Color(1f, 0.55f, 0.25f), heat)));
            GL.End();
        }
    }

    /// <summary>Staging: the stack climbs on its flame and drops its spent stages one after another.</summary>
    internal sealed class StagingEffect : LoadingEffect
    {
        private static readonly float[] StageTop = { 0.40f, 0.62f, 0.78f };      // in screen heights, above the base
        private static readonly float[] StageWidth = { 0.034f, 0.026f, 0.02f };
        private static readonly float[] SeparateAt = { 0.34f, 0.68f };

        public override string Name => "staging";
        public override Color Background => new Color(0.01f, 0.012f, 0.03f, 1f);

        public override void Draw(EffectContext c)
        {
            float w = c.W, h = c.H, p = c.Exiting ? 1f : c.Progress, cx = w * 0.5f, baseY = h * 0.14f;
            if (c.Blend())
            {
                GL.Begin(GL.QUADS);
                Gfx.RectV(0f, 0f, w, h, c.Blended(Gfx.Lerp(new Color(0.2f, 0.35f, 0.6f), new Color(0.01f, 0.015f, 0.04f), p * 1.3f)), c.Blended(new Color(0.005f, 0.008f, 0.02f)));
                GL.End();
            }
            if (c.Additive())
            {
                GL.Begin(GL.QUADS);
                // Stars sliding down: the stack climbs.
                for (int i = 0; i < 160; i++)
                {
                    float x = Noise.Hash(i, 91) * w, y = Mathf.Repeat(Noise.Hash(91, i) - c.T * 0.05f, 1f) * h, s = 1f;
                    Gfx.Rect(x - s, y - s * 3f, x + s, y + s, c.Glow(Color.white, 0.4f * Mathf.Clamp01(p * 2f - 0.3f)));
                }
                GL.End();
            }
            int dropped = p >= SeparateAt[1] ? 2 : p >= SeparateAt[0] ? 1 : 0;
            float stackBase = dropped == 0 ? baseY : baseY + h * StageTop[dropped - 1];
            // Spent stages falling away, tumbling.
            for (int k = 0; k < dropped; k++)
            {
                float since = (p - SeparateAt[k]) * 60f;
                if (since > 6f)
                    continue;
                float bottom = k == 0 ? baseY : baseY + h * StageTop[k - 1];
                float top = baseY + h * StageTop[k];
                float fall = h * 0.06f * since * since, ang = 0.25f * since * (k == 0 ? 1f : -1f);
                DrawStage(c, cx + (k == 0 ? -1f : 1f) * h * 0.02f * since, bottom - fall, top - bottom, StageWidth[k] * h, ang, 1f - since / 6f);
                if (since < 1.2f && c.Additive())
                    Gfx.Fan(cx, top - fall * 0.2f, h * 0.06f * (0.5f + since), h * 0.03f * (0.5f + since), 24, c.Glow(new Color(0.95f, 0.95f, 1f), 0.25f * (1f - since / 1.2f)), Color.clear);
            }
            // Live stack and its flame (relit shortly after each separation).
            for (int k = dropped; k < StageTop.Length; k++)
            {
                float bottom = k == 0 ? baseY : baseY + h * StageTop[k - 1];
                DrawStage(c, cx, bottom, h * StageTop[k] - (bottom - baseY), StageWidth[k] * h, 0f, 1f);
            }
            if (c.Blend())
            {
                GL.Begin(GL.QUADS);
                float tip = baseY + h * StageTop[2];
                Gfx.Quad(cx - StageWidth[2] * h, tip, cx, tip + h * 0.05f, cx, tip + h * 0.05f, cx + StageWidth[2] * h, tip, c.Blended(new Color(0.85f, 0.86f, 0.88f)));
                GL.End();
            }
            float relight = dropped == 0 ? 1f : Mathf.Clamp01(((p - SeparateAt[dropped - 1]) * 60f - 0.6f) / 0.4f);
            if (relight > 0f && c.Additive())
            {
                float fl = 0.85f + 0.15f * Noise.Value(c.T * 14f, 3f), fw = StageWidth[dropped] * h;
                Gfx.Fan(cx, stackBase, fw * 1.2f, h * 0.16f * fl * relight, 24, c.Glow(new Color(1f, 0.9f, 0.7f), 0.9f * relight), c.Glow(new Color(1f, 0.45f, 0.15f), 0f), Mathf.PI * 1.1f, Mathf.PI * 1.9f);
                Gfx.Fan(cx, stackBase, fw * 2.4f, h * 0.3f * fl * relight, 24, c.Glow(new Color(1f, 0.55f, 0.2f), 0.35f * relight), Color.clear, Mathf.PI * 1.15f, Mathf.PI * 1.85f);
            }
        }

        // One stage: a lit cylinder with a dark band, rotated around its centre.
        private static void DrawStage(EffectContext c, float cx, float bottom, float height, float half, float angle, float alpha)
        {
            if (!c.Blend())
                return;
            float my = bottom + height * 0.5f, cs = Mathf.Cos(angle), sn = Mathf.Sin(angle);
            GL.Begin(GL.QUADS);
            Corner(cx, my, -half, -height * 0.5f, cs, sn, out float ax, out float ay);
            Corner(cx, my, -half, height * 0.5f, cs, sn, out float bx, out float by);
            Corner(cx, my, half, height * 0.5f, cs, sn, out float qx, out float qy);
            Corner(cx, my, half, -height * 0.5f, cs, sn, out float dx, out float dy);
            GL.Color(c.Blended(new Color(0.32f, 0.33f, 0.37f), alpha)); GL.Vertex3(ax, ay, 0f); GL.Vertex3(bx, by, 0f);
            GL.Color(c.Blended(new Color(0.88f, 0.89f, 0.92f), alpha)); GL.Vertex3(qx, qy, 0f); GL.Vertex3(dx, dy, 0f);
            Corner(cx, my, -half, height * 0.42f, cs, sn, out ax, out ay);
            Corner(cx, my, half, height * 0.42f, cs, sn, out dx, out dy);
            Gfx.Quad(ax, ay, bx, by, qx, qy, dx, dy, c.Blended(new Color(0.1f, 0.1f, 0.11f), alpha));
            GL.End();
        }

        private static void Corner(float cx, float cy, float x, float y, float cs, float sn, out float ox, out float oy)
        {
            ox = cx + x * cs - y * sn;
            oy = cy + x * sn + y * cs;
        }
    }
}
