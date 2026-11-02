using Hyperdrive.Core;
using UnityEngine;

namespace Hyperdrive.Rendering
{
    /// <summary>Shared bits for the meme scenes.</summary>
    internal static class MemeBits
    {
        // Blend pass set: dips the whole picture to black at both ends of a loop, so it restarts cleanly.
        public static void LoopFade(EffectContext c, float t, float cycle)
        {
            float f = Mathf.Max(1f - t / 0.4f, (t - (cycle - 0.6f)) / 0.6f);
            if (f <= 0f || !c.Blend())
                return;
            GL.Begin(GL.QUADS);
            Gfx.Rect(0f, 0f, c.W, c.H, c.Blended(Color.black, Mathf.Clamp01(f)));
            GL.End();
        }

        // Sets the blend pass: a flame from (x, y) along angle a in two flat colours (orange outside, pale yellow core),
        // as circular sectors so it turns with the craft.
        public static void Flame(EffectContext c, float x, float y, float a, float length, float spread, float strength)
        {
            if (length <= 0f || !c.Blend())
                return;
            Color outer = new Color(0.98f, 0.55f, 0.16f), core = new Color(1f, 0.9f, 0.55f);
            Gfx.Fan(x, y, length, length, 8, c.Blended(outer, strength), c.Blended(outer, strength), a - spread, a + spread);
            Gfx.Fan(x, y, length * 0.55f, length * 0.55f, 6, c.Blended(core, strength), c.Blended(core, strength), a - spread * 0.55f, a + spread * 0.55f);
        }

        // Inside GL.QUADS: daytime sky over hills and grass, with the launch pad.
        public static void Daylight(EffectContext c, float ground, float padX)
        {
            float w = c.W, h = c.H;
            Gfx.Bands(0f, ground, w, h, c.Blended(new Color(0.66f, 0.72f, 0.76f)), c.Blended(new Color(0.3f, 0.43f, 0.6f)), 5);
            SceneBits.Ridge(c, ground + h * 0.05f, h * 0.09f, 0.004f, 3.1f, c.Blended(new Color(0.42f, 0.5f, 0.52f)));
            Gfx.Bands(0f, 0f, w, ground, c.Blended(new Color(0.15f, 0.21f, 0.12f)), c.Blended(new Color(0.24f, 0.31f, 0.18f)), 3);
            Gfx.Rect(padX - h * 0.09f, ground - h * 0.012f, padX + h * 0.09f, ground + h * 0.006f, c.Blended(new Color(0.45f, 0.45f, 0.46f)));
        }
    }

    /// <summary>Fins on the nose: the gravity turn goes a little too far, the rocket flips and tumbles, engine still burning.</summary>
    internal sealed class FlipEffect : LoadingEffect
    {
        private const float Cycle = 8.5f, TurnAt = 2.2f, FlipAt = 3.8f;
        private const int Puffs = 140;
        private readonly float[] _px = new float[Puffs], _py = new float[Puffs], _vx = new float[Puffs], _vy = new float[Puffs], _age = new float[Puffs];
        private float _t0, _emit;
        private int _next, _shownAlt = -1;
        private string _alt = "";

        public override string Name => "flip";
        public override float Boil => 1.6f;
        public override Color Background => new Color(0.2f, 0.33f, 0.55f, 1f);

        public override void Init(EffectContext c, System.Random rng)
        {
            _t0 = c.T;
            for (int i = 0; i < Puffs; i++)
                _age[i] = 99f;
        }

        private float Loop(EffectContext c)
        {
            float lt = c.T - _t0;
            return lt - Mathf.Floor(lt / Cycle) * Cycle;
        }

        // Lean from vertical, positive to the right: steady, a gravity turn, then the flip and a tumble.
        private static float Lean(float t)
        {
            if (t < TurnAt)
                return 0.02f * Mathf.Sin(t * 5f);
            if (t < FlipAt)
            {
                float u = (t - TurnAt) / (FlipAt - TurnAt);
                return 0.35f * u * u;
            }
            float s = t - FlipAt;
            return s < 0.8f ? 0.35f + 2f * s * s : 1.63f + 3.2f * (s - 0.8f);
        }

        // The camera follows the rocket, drifting a little once it tumbles.
        private static Pose RocketPose(EffectContext c, float t) =>
            new Pose(c.W * 0.5f + c.H * 0.04f * Mathf.Max(0f, t - FlipAt), c.H * 0.52f, -Lean(t));

        // How far the scenery has slid down, in screen heights.
        private static float Climb(float t) => t * t * 0.06f;

        public override void Step(EffectContext c)
        {
            float dt = Mathf.Min(c.Dt, 0.1f), t = Loop(c), h = c.H, scroll = 0.12f * t * h;
            for (int i = 0; i < Puffs; i++)
            {
                if (_age[i] > 3f)
                    continue;
                _age[i] += dt;
                _px[i] += _vx[i] * dt;
                _py[i] += (_vy[i] - scroll) * dt;
                _vx[i] *= 1f - 1.5f * dt;
                _vy[i] *= 1f - 1.5f * dt;
            }
            Pose p = RocketPose(c, t);
            float dx = p.Px(0f, -1f) - p.X, dy = p.Py(0f, -1f) - p.Y;
            _emit = Mathf.Min(_emit + dt, 0.5f);   // bounded: a huge or infinite dt must not loop forever
            while (_emit > 0.025f)
            {
                _emit -= 0.025f;
                int i = _next;
                _next = (_next + 1) % Puffs;
                _px[i] = p.Px(0f, -h * 0.16f);
                _py[i] = p.Py(0f, -h * 0.16f);
                _vx[i] = dx * h * 0.35f + (Noise.Hash(i, (int)(t * 40f)) - 0.5f) * h * 0.05f;
                _vy[i] = dy * h * 0.35f;
                _age[i] = 0f;
            }
        }

        public override void Draw(EffectContext c)
        {
            float w = c.W, h = c.H, t = Loop(c), k = Mathf.Clamp01(t / (Cycle * 0.8f)), climb = Climb(t) * h;
            if (!c.Blend())
                return;
            GL.Begin(GL.QUADS);
            Gfx.Bands(0f, 0f, w, h, c.Blended(Gfx.Lerp(new Color(0.62f, 0.72f, 0.82f), new Color(0.03f, 0.05f, 0.12f), k)), c.Blended(Gfx.Lerp(new Color(0.3f, 0.45f, 0.68f), new Color(0.005f, 0.01f, 0.03f), k)), 6);
            float gy = h * 0.2f - climb;
            if (gy > -h * 0.15f)
            {
                SceneBits.Ridge(c, gy + h * 0.05f, h * 0.09f, 0.004f, 3.1f, c.Blended(new Color(0.42f, 0.5f, 0.52f)));
                Gfx.Bands(0f, Mathf.Min(0f, gy - h), w, gy, c.Blended(new Color(0.15f, 0.21f, 0.12f)), c.Blended(new Color(0.24f, 0.31f, 0.18f)), 3);
            }
            GL.End();
            if (k > 0.35f && c.Additive())
            {
                GL.Begin(GL.QUADS);
                SceneBits.Stars(c, 200, 41, 0f, Mathf.Clamp01((k - 0.35f) * 1.6f) * 0.8f);
                GL.End();
            }
            if (!c.Blend())
                return;
            // Clouds at fixed heights, sliding by.
            for (int i = 0; i < 9; i++)
            {
                float y = h * (0.75f + 0.5f * i + 0.2f * Noise.Hash(i, 2)) - climb, x = w * Noise.Hash(i, 3);
                if (y < -h * 0.1f || y > h * 1.1f)
                    continue;
                Color cloud = new Color(0.95f, 0.96f, 0.98f);
                float a = 0.55f * (1f - k);
                Gfx.Fan(x, y, h * 0.12f, h * 0.025f, 20, c.Blended(cloud, a), c.Blended(cloud, a));
                Gfx.Fan(x + h * 0.06f, y + h * 0.012f, h * 0.07f, h * 0.022f, 16, c.Blended(cloud, a), c.Blended(cloud, a));
            }
            for (int i = 0; i < Puffs; i++)
            {
                if (_age[i] > 2.5f)
                    continue;
                float f = _age[i] / 2.5f, r = h * (0.012f + 0.035f * f);
                Comic.Puff(c, _px[i], _py[i], r * 0.8f, new Color(0.92f, 0.92f, 0.9f), 0.7f * (1f - f));
            }
            Pose p = RocketPose(c, t);
            {
                float a = Mathf.Atan2(p.Py(0f, -1f) - p.Y, p.Px(0f, -1f) - p.X), fl = 0.85f + 0.15f * Noise.Value(c.T * 15f, 4f);
                MemeBits.Flame(c, p.Px(0f, -h * 0.155f), p.Py(0f, -h * 0.155f), a, h * 0.1f * fl, 0.16f, 1f);
            }
            if (c.Blend())
            {
                float half = h * 0.022f, top = h * 0.14f, bottom = -h * 0.14f;
                Color steel = c.Blended(Comic.Steel);
                GL.Begin(GL.QUADS);
                p.Trap(bottom - h * 0.014f, bottom, half * 0.8f, half * 0.45f, c.Blended(new Color(0.12f, 0.12f, 0.13f)), c.Blended(new Color(0.3f, 0.3f, 0.32f)));
                p.BoxH(-half, bottom, half, top, c.Blended(Comic.TankShade), c.Blended(Comic.Tank));
                p.Box(-half, bottom + h * 0.09f, half, bottom + h * 0.098f, c.Blended(Comic.Band));
                p.Box(-half, top - h * 0.012f, half, top, c.Blended(Comic.Band));
                // The fins, proudly mounted just under the nose.
                p.Tri(-half, top - h * 0.03f, -half * 2.3f, top - h * 0.1f, -half, top - h * 0.1f, steel);
                p.Tri(half, top - h * 0.03f, half, top - h * 0.1f, half * 2.3f, top - h * 0.1f, steel);
                Color ink = c.Blended(Comic.Ink);
                float iw = Comic.InkWidth(h);
                p.InkBox(-half, bottom, half, top, iw, ink);
                p.InkTrap(bottom - h * 0.014f, bottom, half * 0.8f, half * 0.45f, iw, ink);
                p.InkTri(-half, top - h * 0.03f, -half * 2.3f, top - h * 0.1f, -half, top - h * 0.1f, iw, ink);
                p.InkTri(half, top - h * 0.03f, half, top - h * 0.1f, half * 2.3f, top - h * 0.1f, iw, ink);
                Comic.Capsule(c, new Pose(p.Px(0f, top), p.Py(0f, top), p.Angle), half, h, 1f);
                GL.End();
            }
            MemeBits.LoopFade(c, t, Cycle);
        }

        public override void DrawGui(EffectContext c)
        {
            float t = Loop(c);
            int alt = Mathf.RoundToInt(Climb(t) * 600f) * 10;
            if (alt != _shownAlt)
            {
                _shownAlt = alt;
                _alt = Lang.F("#HD_Alt", alt);
            }
            c.Label(c.W * 0.93f, c.H * 0.2f, _alt, new Color(1f, 1f, 1f, 0.8f), c.H * 0.026f, true);
        }
    }

    /// <summary>TWR 0.87: the engines roar, the smoke piles up, the rocket does not move an inch.</summary>
    internal sealed class TwrEffect : LoadingEffect
    {
        private const int Puffs = 180;
        private readonly float[] _px = new float[Puffs], _py = new float[Puffs], _vx = new float[Puffs], _vy = new float[Puffs], _age = new float[Puffs], _life = new float[Puffs];
        private System.Random _rng;
        private float _emit;
        private int _next;

        public override string Name => "twr";
        public override float Boil => 1.6f;
        public override Color Background => new Color(0.06f, 0.07f, 0.1f, 1f);

        public override void Init(EffectContext c, System.Random rng)
        {
            _rng = rng;
            for (int i = 0; i < Puffs; i++)
                _age[i] = 99f;
            // It has been burning for a while already.
            for (int s = 0; s < 120; s++)
                Advance(c, 0.03f);
        }

        public override void Step(EffectContext c) => Advance(c, Mathf.Min(c.Dt, 0.1f));

        private void Advance(EffectContext c, float dt)
        {
            float h = c.H, ground = h * 0.2f;
            for (int i = 0; i < Puffs; i++)
            {
                if (_age[i] > _life[i])
                    continue;
                _age[i] += dt;
                _px[i] += _vx[i] * dt;
                _py[i] += _vy[i] * dt;
                _vx[i] *= 1f - 0.9f * dt;
                _vy[i] += h * 0.006f * dt;
            }
            _emit = Mathf.Min(_emit + dt, 0.5f);
            while (_emit > 0.035f)
            {
                _emit -= 0.035f;
                int i = _next;
                _next = (_next + 1) % Puffs;
                float side = Rand(_rng) < 0.5f ? -1f : 1f;
                _px[i] = c.W * 0.6f + side * h * 0.03f;
                _py[i] = ground + h * 0.01f;
                _vx[i] = side * Rand(_rng, 0.25f, 0.6f) * h;
                _vy[i] = Rand(_rng, 0.01f, 0.05f) * h;
                _age[i] = 0f;
                _life[i] = Rand(_rng, 5f, 8f);
            }
        }

        public override void Draw(EffectContext c)
        {
            float w = c.W, h = c.H, ground = h * 0.2f, cx = w * 0.6f, half = h * 0.05f, fl = 0.8f + 0.2f * Noise.Value(c.T * 18f, 6f);
            if (!c.Blend())
                return;
            GL.Begin(GL.QUADS);
            Gfx.Bands(0f, ground, w, h, c.Blended(new Color(0.42f, 0.36f, 0.38f)), c.Blended(new Color(0.08f, 0.1f, 0.18f)), 6);
            SceneBits.Ridge(c, ground + h * 0.04f, h * 0.08f, 0.004f, 6.2f, c.Blended(new Color(0.16f, 0.17f, 0.2f)));
            Gfx.Bands(0f, 0f, w, ground, c.Blended(new Color(0.07f, 0.08f, 0.07f)), c.Blended(new Color(0.14f, 0.15f, 0.13f)), 3);
            Gfx.Rect(cx - h * 0.14f, ground - h * 0.012f, cx + h * 0.14f, ground + h * 0.006f, c.Blended(new Color(0.32f, 0.32f, 0.33f)));
            GL.End();
            if (c.Blend())
                Gfx.Fan(cx, ground, h * 0.3f, h * 0.05f, 24, c.Blended(new Color(0.85f, 0.45f, 0.18f), 0.35f * fl), c.Blended(new Color(0.85f, 0.45f, 0.18f), 0.35f * fl), 0f, Mathf.PI);
            // Shaking on the spot.
            float rx = cx + h * 0.0025f * Mathf.Sin(c.T * 61f), ry = ground + h * 0.016f + h * 0.0015f * Mathf.Abs(Mathf.Sin(c.T * 43f)), top = ry + h * 0.3f;
            for (int e = -1; e <= 1; e++)
                MemeBits.Flame(c, rx + e * half * 0.6f, ry - h * 0.014f, -Mathf.PI * 0.5f, h * 0.07f * fl, 0.22f, 1f);
            if (c.Blend())
            {
                var p = new Pose(rx, ry, 0f);
                GL.Begin(GL.QUADS);
                for (int e = -1; e <= 1; e++)
                {
                    var bell = new Pose(rx + e * half * 0.6f, ry, 0f);
                    bell.Trap(-h * 0.016f, 0f, half * 0.3f, half * 0.18f, c.Blended(new Color(0.12f, 0.12f, 0.13f)), c.Blended(new Color(0.3f, 0.3f, 0.32f)));
                }
                // A big orange tank, as it should be.
                p.BoxH(-half, 0f, half, h * 0.3f, c.Blended(new Color(0.42f, 0.22f, 0.08f)), c.Blended(new Color(0.85f, 0.5f, 0.2f)));
                p.Box(-half, h * 0.14f, half, h * 0.15f, c.Blended(Comic.Band));
                p.Box(-half, h * 0.29f, half, h * 0.3f, c.Blended(Comic.Band));
                p.InkBox(-half, 0f, half, h * 0.3f, Comic.InkWidth(h), c.Blended(Comic.Ink));
                Comic.Capsule(c, new Pose(rx, top, 0f), half * 0.75f, h * 1.3f, 1f);
                GL.End();
            }
            if (c.Blend())
                for (int i = 0; i < Puffs; i++)
                {
                    if (_age[i] > _life[i])
                        continue;
                    float f = _age[i] / _life[i], r = h * (0.03f + 0.08f * f), a = 0.8f * Mathf.Min(1f, f * 10f) * (1f - f);
                    Color col = Gfx.Lerp(new Color(0.95f, 0.75f, 0.55f), new Color(0.82f, 0.82f, 0.8f), (_py[i] - ground) / (h * 0.15f));
                    Comic.Puff(c, _px[i], _py[i] + r * 0.3f, r, col, a);
                }
        }

        public override void DrawGui(EffectContext c)
        {
            c.Label(c.W * 0.93f, c.H * 0.92f, c.Tr("#HD_TWR"), new Color(1f, 0.9f, 0.8f, 0.8f), c.H * 0.026f, true);   // top right: the load problems notice takes the top left
        }
    }

    /// <summary>Stage 1: parachute. It opens on the pad, catches the wind and pulls the rocket over.</summary>
    internal sealed class ChuteEffect : LoadingEffect
    {
        private const float Cycle = 9f, Pop = 1.4f, TipStart = 2.6f, TipEnd = 4.1f;
        private float _t0;

        public override string Name => "chute";
        public override float Boil => 1.6f;
        public override Color Background => new Color(0.4f, 0.5f, 0.62f, 1f);

        public override void Init(EffectContext c, System.Random rng) => _t0 = c.T;

        private float Loop(EffectContext c)
        {
            float lt = c.T - _t0;
            return lt - Mathf.Floor(lt / Cycle) * Cycle;
        }

        public override void Draw(EffectContext c)
        {
            float w = c.W, h = c.H, ground = h * 0.2f, cx = w * 0.42f, half = h * 0.024f, t = Loop(c);
            if (!c.Blend())
                return;
            GL.Begin(GL.QUADS);
            MemeBits.Daylight(c, ground, cx);
            GL.End();
            // Falls over to the right, around the tip of its right fin.
            float u = Mathf.Clamp01((t - TipStart) / (TipEnd - TipStart)), ang = -Mathf.PI * 0.5f * u * u;
            if (t > TipEnd)
                ang = -Mathf.PI * 0.5f + 0.05f * Mathf.Sin((t - TipEnd) * 14f) * Mathf.Exp(-5f * (t - TipEnd));
            float pivX = cx + half * 2.1f, pivY = ground, ox = -half * 2.1f, oy = h * 0.014f;
            float cs = Mathf.Cos(ang), sn = Mathf.Sin(ang);
            var p = new Pose(pivX + ox * cs - oy * sn, pivY + ox * sn + oy * cs, ang);
            if (t > TipEnd && t < TipEnd + 1.2f)
            {
                float d = (t - TipEnd) / 1.2f;
                for (int i = 0; i < 5; i++)
                {
                    float r = h * (0.02f + 0.05f * d), x = pivX + h * (0.04f + 0.06f * i);
                    Comic.Puff(c, x, ground + r * 0.3f, r * 0.8f, new Color(0.75f, 0.72f, 0.62f), 0.75f * (1f - d));
                }
            }
            GL.Begin(GL.QUADS);
            Comic.Rocket(c, p, half, h, 1f);
            GL.End();
            if (t > Pop)
            {
                float k = Mathf.Clamp01((t - Pop) / 0.5f), wind = Mathf.Clamp01((t - Pop) / 2f);
                float ax = p.Px(0f, h * 0.312f), ay = p.Py(0f, h * 0.312f);
                // The canopy rides the wind to the right, then settles on the grass, deflated.
                float down = Mathf.Clamp01((t - TipStart) / (TipEnd - TipStart + 0.6f));
                float qx = ax + h * (0.05f + 0.14f * wind), qy = Mathf.Max(ground + h * 0.01f, ay + h * 0.15f * k * (1f - down) + h * 0.02f * Mathf.Sin(t * 2.3f) * (1f - down));
                float inflate = k * (1f - 0.8f * Mathf.Clamp01((t - TipEnd) / 0.8f)), tilt = -0.45f * wind * (1f - down);
                float r = h * 0.08f * Mathf.Max(k, 0.3f);
                GL.Begin(GL.LINES);
                Color line = c.Blended(new Color(0.9f, 0.9f, 0.88f), 0.8f);
                Gfx.Line(ax, ay, qx + Mathf.Cos(Mathf.PI + tilt) * r, qy + Mathf.Sin(Mathf.PI + tilt) * r * 0.6f, line);
                Gfx.Line(ax, ay, qx + Mathf.Cos(tilt) * r, qy + Mathf.Sin(tilt) * r * 0.6f, line);
                Gfx.Line(ax, ay, qx, qy, line);
                GL.End();
                for (int s = 0; s < 6; s++)
                {
                    float a0 = tilt + Mathf.PI * s / 6f, a1 = a0 + Mathf.PI / 6f;
                    Color col = (s & 1) == 0 ? new Color(0.95f, 0.5f, 0.15f) : new Color(0.95f, 0.94f, 0.9f);
                    Gfx.Fan(qx, qy, r, r * 0.6f * Mathf.Max(inflate, 0.15f), 6, c.Blended(col), c.Blended(col), a0, a1);
                }
                if (t - Pop < 0.5f)
                {
                    float d = (t - Pop) / 0.5f, pr = h * (0.02f + 0.05f * d);
                    Comic.Puff(c, ax, ay + h * 0.01f, pr, Color.white, 0.8f * (1f - d));
                }
            }
            MemeBits.LoopFade(c, t, Cycle);
        }
    }

    /// <summary>The main gear sits too far back: the plane cannot rotate, runs out of runway and becomes a seaplane.</summary>
    internal sealed class RunwayEffect : LoadingEffect
    {
        private const float Cycle = 9f, EdgeAt = 4.2f, MaxSpeed = 112f;
        private float _t0;
        private int _shownSpeed = -1;
        private string _speed = "";

        public override string Name => "runway";
        public override float Boil => 1.6f;
        public override Color Background => new Color(0.4f, 0.55f, 0.72f, 1f);

        public override void Init(EffectContext c, System.Random rng) => _t0 = c.T;

        private float Loop(EffectContext c)
        {
            float lt = c.T - _t0;
            return lt - Mathf.Floor(lt / Cycle) * Cycle;
        }

        // Plane pose at loop time t; phase 0 rolling, 1 falling, 2 floating. 'since' is the time spent in that phase.
        private static void State(EffectContext c, float t, out float x, out float y, out float ang, out int phase, out float since)
        {
            float w = c.W, h = c.H, cliff = w * 0.62f, gy = h * 0.32f, sea = h * 0.14f, x0 = w * 0.05f;
            float a = 2f * (cliff - x0) / (EdgeAt * EdgeAt), v = a * EdgeAt, g = h * 1.2f;
            if (t < EdgeAt)
            {
                phase = 0;
                since = t;
                x = x0 + 0.5f * a * t * t;
                y = gy + h * 0.03f + h * 0.0015f * Mathf.Sin(t * 25f) * Mathf.Min(1f, t);
                ang = -0.01f;
                return;
            }
            float s = t - EdgeAt, splash = Mathf.Sqrt(2f * (gy + h * 0.03f - sea) / g);
            if (s < splash)
            {
                phase = 1;
                since = s;
                x = cliff + v * 0.9f * s;
                y = gy + h * 0.03f - 0.5f * g * s * s;
                ang = -Mathf.Min(0.9f, 1.1f * s);
                return;
            }
            phase = 2;
            since = s - splash;
            float landA = -Mathf.Min(0.9f, 1.1f * splash);
            x = cliff + v * 0.9f * splash + v * 0.12f * (1f - Mathf.Exp(-3f * since));
            y = sea - h * 0.004f + h * 0.006f * Mathf.Sin(since * 2.5f) - h * 0.03f * Mathf.Exp(-3f * since);
            ang = Mathf.Lerp(landA, -0.12f, 1f - Mathf.Exp(-2.5f * since));
        }

        public override void Draw(EffectContext c)
        {
            float w = c.W, h = c.H, t = Loop(c), cliff = w * 0.62f, gy = h * 0.32f, sea = h * 0.14f;
            State(c, t, out float x, out float y, out float ang, out int phase, out float since);
            if (!c.Blend())
                return;
            GL.Begin(GL.QUADS);
            Gfx.Bands(0f, sea, w, h, c.Blended(new Color(0.72f, 0.78f, 0.82f)), c.Blended(new Color(0.32f, 0.48f, 0.68f)), 6);
            SceneBits.Ridge(c, gy + h * 0.06f, h * 0.1f, 0.003f, 2.2f, c.Blended(new Color(0.45f, 0.53f, 0.57f)));
            Gfx.Bands(cliff, 0f, w, sea, c.Blended(new Color(0.1f, 0.22f, 0.36f)), c.Blended(new Color(0.2f, 0.36f, 0.5f)), 3);
            Gfx.Bands(0f, 0f, cliff, gy, c.Blended(new Color(0.16f, 0.22f, 0.13f)), c.Blended(new Color(0.26f, 0.34f, 0.2f)), 3);
            Gfx.Quad(cliff, 0f, cliff, gy, cliff + w * 0.012f, gy - h * 0.06f, cliff + w * 0.025f, 0f, c.Blended(new Color(0.3f, 0.29f, 0.27f)));
            // Runway, its centre line, and its very clear end.
            Gfx.Rect(w * 0.02f, gy - h * 0.006f, cliff - w * 0.008f, gy, c.Blended(new Color(0.22f, 0.22f, 0.23f)));
            for (float d = w * 0.04f; d < cliff - w * 0.03f; d += w * 0.05f)
                Gfx.Rect(d, gy - h * 0.0035f, d + w * 0.025f, gy - h * 0.0025f, c.Blended(new Color(0.9f, 0.9f, 0.88f)));
            GL.End();
            var p = new Pose(x, y, ang);
            if (phase < 2)
                MemeBits.Flame(c, p.Px(-h * 0.1f, h * 0.002f), p.Py(-h * 0.1f, h * 0.002f), Mathf.PI + ang, h * 0.035f * (0.85f + 0.15f * Noise.Value(c.T * 15f, 9f)), 0.14f, 0.9f);
            if (c.Blend())
            {
                Color hull = c.Blended(new Color(0.86f, 0.86f, 0.88f)), steel = c.Blended(Comic.Steel), dark = c.Blended(new Color(0.12f, 0.12f, 0.13f));
                GL.Begin(GL.QUADS);
                p.Thick(h * 0.055f, -h * 0.008f, h * 0.055f, -h * 0.022f, h * 0.003f, steel);
                p.Thick(-h * 0.075f, -h * 0.008f, -h * 0.075f, -h * 0.022f, h * 0.003f, steel);
                p.Box(-h * 0.09f, -h * 0.008f, h * 0.07f, h * 0.014f, hull);
                p.Box(-h * 0.09f, h * 0.008f, h * 0.07f, h * 0.014f, c.Blended(Color.white));
                p.Tri(h * 0.07f, -h * 0.008f, h * 0.102f, -h * 0.001f, h * 0.07f, h * 0.014f, hull);
                p.Tri(h * 0.035f, h * 0.014f, h * 0.066f, h * 0.014f, h * 0.045f, h * 0.024f, c.Blended(new Color(0.08f, 0.12f, 0.2f)));
                p.Tri(-h * 0.09f, h * 0.014f, -h * 0.06f, h * 0.014f, -h * 0.098f, h * 0.05f, steel);
                p.Box(-h * 0.1f, -h * 0.006f, -h * 0.09f, h * 0.01f, dark);
                p.Box(-h * 0.035f, -h * 0.001f, h * 0.03f, h * 0.005f, c.Blended(new Color(0.55f, 0.56f, 0.6f)));
                Color ink = c.Blended(Comic.Ink);
                float iw = Comic.InkWidth(h);
                p.InkBox(-h * 0.09f, -h * 0.008f, h * 0.07f, h * 0.014f, iw, ink);
                p.InkTri(h * 0.07f, -h * 0.008f, h * 0.102f, -h * 0.001f, h * 0.07f, h * 0.014f, iw, ink);
                p.InkTri(-h * 0.09f, h * 0.014f, -h * 0.06f, h * 0.014f, -h * 0.098f, h * 0.05f, iw, ink);
                GL.End();
                for (int g = 0; g < 2; g++)
                {
                    float gx = g == 0 ? h * 0.055f : -h * 0.075f, wx = p.Px(gx, -h * 0.025f), wy = p.Py(gx, -h * 0.025f);
                    Gfx.Fan(wx, wy, h * 0.0065f, h * 0.0065f, 12, c.Blended(new Color(0.2f, 0.2f, 0.21f)), dark);
                }
                // Water over the part that sank.
                if (phase == 2)
                {
                    GL.Begin(GL.QUADS);
                    Gfx.Bands(cliff + w * 0.025f, 0f, w, sea, c.Blended(new Color(0.1f, 0.22f, 0.36f), 0.75f), c.Blended(new Color(0.2f, 0.36f, 0.5f), 0.75f), 3);
                    GL.End();
                    if (since < 1.3f)
                    {
                        float d = since / 1.3f;
                        for (int i = 0; i < 7; i++)
                        {
                            float sx = x + (Noise.Hash(i, 5) - 0.5f) * h * 0.12f * (0.4f + d), sy = sea + h * 0.06f * Mathf.Sin(Mathf.PI * Mathf.Min(1f, d * (1.2f + Noise.Hash(i, 6))));
                            float r = h * (0.012f + 0.02f * Noise.Hash(i, 7));
                            Comic.Puff(c, sx, sy, r, new Color(0.95f, 0.97f, 1f), 0.9f * (1f - d));
                        }
                    }
                }
            }
            MemeBits.LoopFade(c, t, Cycle);
        }

        public override void DrawGui(EffectContext c)
        {
            float t = Loop(c);
            State(c, t, out _, out _, out _, out int phase, out _);
            int speed = phase == 2 ? 0 : Mathf.RoundToInt(MaxSpeed * Mathf.Min(t, EdgeAt) / EdgeAt);
            if (speed != _shownSpeed)
            {
                _shownSpeed = speed;
                _speed = Lang.F("#HD_Speed", speed);
            }
            c.Label(c.W * 0.93f, c.H * 0.92f, _speed, new Color(1f, 1f, 1f, 0.8f), c.H * 0.026f, true);   // top right: the load problems notice takes the top left
        }
    }

    /// <summary>Kerbin and its orbit, filling up with debris as the game loads.</summary>
    internal sealed class DebrisEffect : LoadingEffect
    {
        private const int Max = 700;
        private int _shown = -1;
        private string _count = "";

        public override string Name => "debris";
        public override float Boil => 1.6f;
        public override Color Background => new Color(0f, 0f, 0.006f, 1f);

        private static float Fill(EffectContext c) => c.Exiting ? 1f : Mathf.Clamp01(c.Progress);

        // Position of piece i on its own tilted, flattened orbit; true when it passes in front of the planet.
        private static bool Piece(EffectContext c, int i, float cx, float cy, float radius, out float x, out float y)
        {
            float r = radius * (1.12f + Noise.Hash(i, 1)), tilt = Noise.Hash(i, 2) * Mathf.PI, flat = 0.12f + 0.88f * Noise.Hash(i, 3);
            float speed = 0.35f / Mathf.Pow(r / radius, 1.5f) * (Noise.Hash(i, 6) < 0.08f ? -1f : 1f);
            float th = Noise.Hash(i, 4) * Gfx.Tau + c.T * speed, lx = Mathf.Cos(th) * r, ly = Mathf.Sin(th) * r * flat;
            float ct = Mathf.Cos(tilt), st = Mathf.Sin(tilt);
            x = cx + lx * ct - ly * st;
            y = cy + lx * st + ly * ct;
            return Mathf.Sin(th) < 0f;
        }

        private static void Pieces(EffectContext c, int n, float cx, float cy, float radius, bool front)
        {
            GL.Begin(GL.QUADS);
            for (int i = 0; i < n; i++)
            {
                bool inFront = Piece(c, i, cx, cy, radius, out float x, out float y);
                if (inFront != front)
                    continue;
                if (!front && (x - cx) * (x - cx) + (y - cy) * (y - cy) < radius * radius)
                    continue;
                float s = c.H * (0.0012f + 0.0018f * Noise.Hash(i, 5));
                Gfx.Rect(x - s, y - s, x + s, y + s, c.Glow(new Color(0.9f, 0.9f, 0.86f), 0.6f + 0.4f * Noise.Hash(i, 8)));
            }
            GL.End();
        }

        public override void Draw(EffectContext c)
        {
            float w = c.W, h = c.H, cx = w * 0.4f, cy = h * 0.52f, radius = h * 0.2f;
            int n = 80 + Mathf.RoundToInt(Fill(c) * (Max - 80));
            if (c.Additive())
            {
                GL.Begin(GL.QUADS);
                SceneBits.Stars(c, 220, 83, 0f, 0.6f);
                GL.End();
                Pieces(c, n, cx, cy, radius, false);
            }
            if (c.Blend())
            {
                Gfx.Fan(cx, cy, radius * 1.03f, radius * 1.03f, 64, c.Blended(new Color(0.45f, 0.62f, 0.9f)), c.Blended(new Color(0.45f, 0.62f, 0.9f)));
                Gfx.Fan(cx, cy, radius, radius, 64, c.Blended(new Color(0.2f, 0.4f, 0.72f)), c.Blended(new Color(0.2f, 0.4f, 0.72f)));
                Gfx.Fan(cx - radius * 0.3f, cy + radius * 0.25f, radius * 0.35f, radius * 0.22f, 20, c.Blended(new Color(0.3f, 0.5f, 0.24f)), c.Blended(new Color(0.3f, 0.5f, 0.24f)));
                Gfx.Fan(cx + radius * 0.35f, cy - radius * 0.2f, radius * 0.25f, radius * 0.3f, 20, c.Blended(new Color(0.3f, 0.5f, 0.24f)), c.Blended(new Color(0.3f, 0.5f, 0.24f)));
                Gfx.Fan(cx, cy, radius * 1.005f, radius * 1.005f, 64, c.Blended(Color.black, 0.6f), c.Blended(Color.black, 0.6f), Mathf.PI * 0.5f + 0.4f, Mathf.PI * 1.5f + 0.4f);
            }
            if (c.Additive())
            {
                Pieces(c, n, cx, cy, radius, true);
                // Now and then two pieces meet, and there are more pieces.
                int beat = Mathf.FloorToInt(c.T * 0.8f);
                float f = c.T * 0.8f - beat;
                if (f < 0.2f)
                    for (int j = 0; j < 2; j++)
                    {
                        int i = (int)(Noise.Hash(j, beat) * n);
                        if (Piece(c, i, cx, cy, radius, out float x, out float y))
                        {
                            float r = h * 0.014f * (1f - f / 0.2f);
                            Gfx.Fan(x, y, r, r, 8, c.Glow(new Color(1f, 0.8f, 0.5f)), c.Glow(new Color(1f, 0.8f, 0.5f)));
                        }
                    }
            }
        }

        public override void DrawGui(EffectContext c)
        {
            int count = 12 + Mathf.RoundToInt(Fill(c) * 4809f);
            if (count != _shown)
            {
                _shown = count;
                _count = Lang.F("#HD_DebrisCount", count);
            }
            c.Label(c.W * 0.93f, c.H * 0.2f, _count, new Color(0.9f, 0.9f, 0.85f, 0.75f), c.H * 0.026f, true);
        }
    }
}
