using UnityEngine;

namespace Hyperdrive.Rendering
{
    /// <summary>Rain on a window at night: blurred city lights behind the glass, drops that run in fits and starts.</summary>
    internal sealed class RainEffect : LoadingEffect
    {
        private const int Lights = 28, Beads = 150, Runners = 16;
        private readonly float[] _ry = new float[Runners], _rv = new float[Runners];

        public override string Name => "rain";
        public override Color Background => new Color(0.06f, 0.07f, 0.1f, 1f);

        public override void Init(EffectContext c, System.Random rng)
        {
            for (int i = 0; i < Runners; i++)
                _ry[i] = Rand(rng);
        }

        public override void Step(EffectContext c)
        {
            for (int i = 0; i < Runners; i++)
            {
                // Stop and go: a drop waits until it has gathered enough water, then slides.
                bool sliding = Noise.Value(c.T * 0.7f + i * 3.1f, i) > 0.55f;
                _rv[i] = sliding ? 0.05f + 0.06f * Noise.Hash(i, 3) : 0.002f;
                _ry[i] -= _rv[i] * c.Dt;
                if (_ry[i] < -0.05f)
                    _ry[i] = 1.05f;
            }
        }

        public override void Draw(EffectContext c)
        {
            float w = c.W, h = c.H;
            if (!c.Blend())
                return;
            // Out-of-focus lights: soft discs, warm and a few cold, breathing slightly.
            for (int i = 0; i < Lights; i++)
            {
                float x = Noise.Hash(i, 1) * w, y = h * (0.15f + 0.6f * Noise.Hash(1, i));
                float r = h * (0.03f + 0.06f * Noise.Hash(i, 2));
                float k = Noise.Hash(i, 4);
                Color col = k < 0.55f ? new Color(1f, 0.72f, 0.38f) : k < 0.8f ? new Color(0.95f, 0.88f, 0.75f) : k < 0.92f ? new Color(0.45f, 0.75f, 0.85f) : new Color(0.95f, 0.35f, 0.3f);
                float a = (0.16f + 0.12f * Noise.Hash(i, 5)) * (0.85f + 0.15f * Mathf.Sin(c.T * 0.4f + i));
                Gfx.Fan(x, y, r, r, 24, c.Blended(col, a), c.Blended(col, a * 0.6f));
            }
            // Beads of water on the glass: a dark rim below, a bright rim above.
            GL.Begin(GL.QUADS);
            for (int i = 0; i < Beads; i++)
            {
                float x = Noise.Hash(i, 11) * w, y = Noise.Hash(11, i) * h, s = h * (0.0015f + 0.003f * Noise.Hash(i, 12));
                Gfx.Rect(x - s, y - s, x + s, y, c.Blended(new Color(0f, 0f, 0f), 0.25f));
                Gfx.Rect(x - s * 0.6f, y, x + s * 0.6f, y + s * 0.7f, c.Blended(new Color(0.85f, 0.88f, 0.95f), 0.35f));
            }
            // Running drops and the wet trail they leave.
            for (int i = 0; i < Runners; i++)
            {
                float x = w * (0.04f + 0.92f * Noise.Hash(i, 21)) + Mathf.Sin(_ry[i] * 9f + i) * h * 0.004f;
                float y = _ry[i] * h, s = h * 0.004f;
                Gfx.RectV(x - s * 0.25f, y, x + s * 0.25f, Mathf.Min(h, y + h * 0.12f), c.Blended(new Color(0.8f, 0.85f, 0.95f), 0.12f), c.Blended(new Color(0.8f, 0.85f, 0.95f), 0f));
                Gfx.Rect(x - s, y - s * 1.3f, x + s, y + s * 0.4f, c.Blended(new Color(0f, 0f, 0f), 0.3f));
                Gfx.Rect(x - s * 0.6f, y, x + s * 0.6f, y + s, c.Blended(new Color(0.9f, 0.92f, 1f), 0.45f));
            }
            // Window frame, off-centre.
            Color frame = c.Blended(new Color(0.02f, 0.02f, 0.03f));
            Gfx.Rect(w * 0.64f, 0f, w * 0.64f + h * 0.025f, h, frame);
            Gfx.Rect(0f, h * 0.38f, w, h * 0.38f + h * 0.02f, frame);
            GL.End();
        }
    }

    /// <summary>A misty pine forest: ridges of trees fading into the fog, fog banks drifting across.</summary>
    internal sealed class ForestEffect : LoadingEffect
    {
        public override string Name => "forest";
        public override Color Background => new Color(0.8f, 0.83f, 0.81f, 1f);

        public override void Draw(EffectContext c)
        {
            float w = c.W, h = c.H, fog = c.Exiting ? 0.4f : 1f - 0.5f * c.Progress;
            if (!c.Blend())
                return;
            GL.Begin(GL.QUADS);
            Gfx.RectV(0f, 0f, w, h, c.Blended(new Color(0.86f, 0.87f, 0.84f)), c.Blended(new Color(0.7f, 0.75f, 0.76f)));
            GL.End();
            Color far = new Color(0.62f, 0.68f, 0.68f), near = new Color(0.1f, 0.14f, 0.13f);
            for (int layer = 0; layer < 5; layer++)
            {
                float d = layer / 4f;
                Color col = Color.Lerp(far, near, d * d);
                float baseY = h * (0.58f - 0.12f * layer), scale = h * (0.05f + 0.05f * layer);
                float drift = c.T * w * 0.002f * (layer + 1);
                Trees(c, baseY, scale, drift, layer * 17 + 3, c.Blended(col));
                // A fog bank between this ridge and the next.
                if (layer < 4)
                {
                    float fy = baseY - scale * 0.6f, fh = h * 0.08f;
                    float off = Mathf.Sin(c.T * 0.05f + layer) * w * 0.05f;
                    GL.Begin(GL.QUADS);
                    for (int k = 0; k < 3; k++)
                    {
                        float x0 = Mathf.Repeat(Noise.Hash(k, layer) * w + off + c.T * w * 0.004f * (k + 1), w * 1.6f) - w * 0.3f;
                        Gfx.RectV(x0, fy - fh, x0 + w * 0.6f, fy + fh, c.Blended(new Color(0.85f, 0.87f, 0.85f), 0.5f * fog), c.Blended(new Color(0.85f, 0.87f, 0.85f), 0f));
                    }
                    GL.End();
                }
            }
        }

        // A row of pines along a gentle ridge.
        private static void Trees(EffectContext c, float baseY, float scale, float drift, int seed, Color col)
        {
            float w = c.W;
            GL.Begin(GL.QUADS);
            Gfx.Rect(0f, -10f, w, baseY, col);
            GL.End();
            GL.Begin(GL.TRIANGLES);
            GL.Color(col);
            float spacing = scale * 0.55f;
            int n = Mathf.CeilToInt(w / spacing) + 2;
            float start = -Mathf.Repeat(drift, spacing);
            int first = Mathf.FloorToInt(drift / spacing);
            for (int i = -1; i < n; i++)
            {
                int id = i + first;
                float x = start + i * spacing + spacing * 0.4f * (Noise.Hash(id, seed) - 0.5f);
                float hh = scale * (0.7f + 0.8f * Noise.Hash(seed, id));
                float y = baseY + Mathf.Sin(id * 0.3f + seed) * scale * 0.15f;
                // Three stacked triangles make a fir.
                for (int t = 0; t < 3; t++)
                {
                    float ty = y + hh * t * 0.28f, tw = scale * (0.28f - 0.07f * t);
                    GL.Vertex3(x - tw, ty, 0f); GL.Vertex3(x, ty + hh * 0.5f, 0f); GL.Vertex3(x + tw, ty, 0f);
                }
            }
            GL.End();
        }
    }

    /// <summary>A campfire at night: flat flame tongues, sparks rising and dying, logs, the edge of the woods.</summary>
    internal sealed class CampfireEffect : LoadingEffect
    {
        private const int Sparks = 60;

        public override string Name => "campfire";
        public override Color Background => new Color(0.025f, 0.025f, 0.035f, 1f);

        public override void Draw(EffectContext c)
        {
            float w = c.W, h = c.H, fx = w * 0.42f, fy = h * 0.24f, t = Mathf.Floor(c.T * 12f) / 12f;
            if (!c.Blend())
                return;
            GL.Begin(GL.QUADS);
            Gfx.RectV(0f, fy - h * 0.02f, w, h, c.Blended(new Color(0.07f, 0.06f, 0.07f)), c.Blended(new Color(0.02f, 0.025f, 0.04f)));
            Gfx.Rect(0f, 0f, w, fy - h * 0.02f, c.Blended(new Color(0.05f, 0.04f, 0.035f)));
            for (int i = 0; i < 70; i++)
            {
                float x = Noise.Hash(i, 31) * w, y = h * (0.55f + 0.45f * Noise.Hash(31, i)), s = h * 0.0013f;
                Gfx.Rect(x - s, y - s, x + s, y + s, c.Blended(new Color(0.9f, 0.9f, 0.85f), 0.25f + 0.4f * Noise.Hash(i, 32)));
            }
            GL.End();
            // Warm light on the ground, one soft pool.
            if (c.Additive())
                Gfx.Fan(fx, fy, h * 0.4f, h * 0.1f, 40, c.Glow(new Color(1f, 0.5f, 0.2f), 0.22f * (0.9f + 0.1f * Noise.Value(t * 7f, 1f))), Color.clear);
            if (!c.Blend())
                return;
            // Trees framing the clearing.
            GL.Begin(GL.TRIANGLES);
            GL.Color(c.Blended(new Color(0.012f, 0.014f, 0.018f)));
            for (int i = 0; i < 9; i++)
            {
                bool left = i < 4;
                float x = left ? w * (0.02f + 0.05f * i) : w * (0.78f + 0.05f * (i - 4)), hh = h * (0.35f + 0.25f * Noise.Hash(i, 3));
                for (int k = 0; k < 4; k++)
                {
                    float ty = fy + hh * k * 0.22f, tw = h * (0.08f - 0.015f * k);
                    GL.Vertex3(x - tw, ty, 0f); GL.Vertex3(x, ty + hh * 0.4f, 0f); GL.Vertex3(x + tw, ty, 0f);
                }
            }
            GL.End();
            // Logs.
            GL.Begin(GL.QUADS);
            Gfx.Thick(fx - h * 0.09f, fy - h * 0.01f, fx + h * 0.07f, fy + h * 0.02f, h * 0.025f, c.Blended(new Color(0.22f, 0.13f, 0.08f)));
            Gfx.Thick(fx + h * 0.09f, fy - h * 0.01f, fx - h * 0.07f, fy + h * 0.025f, h * 0.025f, c.Blended(new Color(0.18f, 0.1f, 0.06f)));
            GL.End();
            // Flame tongues: flat shapes in three colours, redrawn on twos.
            Tongue(c, fx, fy + h * 0.01f, h * 0.05f, h * 0.2f, t, 0, new Color(0.85f, 0.25f, 0.08f));
            Tongue(c, fx - h * 0.02f, fy + h * 0.01f, h * 0.035f, h * 0.15f, t, 1, new Color(0.98f, 0.55f, 0.12f));
            Tongue(c, fx + h * 0.02f, fy + h * 0.01f, h * 0.03f, h * 0.12f, t, 2, new Color(0.98f, 0.55f, 0.12f));
            Tongue(c, fx, fy + h * 0.01f, h * 0.02f, h * 0.08f, t, 3, new Color(1f, 0.86f, 0.4f));
            // Sparks: rise, sway, fade.
            GL.Begin(GL.QUADS);
            for (int i = 0; i < Sparks; i++)
            {
                float life = 2f + 2f * Noise.Hash(i, 41);
                float age = Mathf.Repeat(c.T + Noise.Hash(41, i) * life, life) / life;
                float x = fx + (Noise.Hash(i, 42) - 0.5f) * h * 0.08f + Mathf.Sin(c.T * 1.7f + i) * h * 0.02f * age;
                float y = fy + h * 0.06f + age * h * (0.25f + 0.25f * Noise.Hash(i, 43));
                float s = h * 0.0025f * (1f - age);
                Gfx.Rect(x - s, y - s, x + s, y + s, c.Blended(new Color(1f, 0.6f + 0.3f * (1f - age), 0.2f), 1f - age));
            }
            GL.End();
        }

        // A flame tongue: a pointed, wavering shape whose tip leans with a slow draft.
        private static void Tongue(EffectContext c, float x, float y, float r, float len, float t, int seed, Color col)
        {
            float lean = 0.25f * (Noise.Value(t * 1.3f, seed) - 0.5f);
            float l = len * (0.8f + 0.4f * Noise.Value(t * 5f, seed + 7));
            GL.Begin(GL.TRIANGLES);
            GL.Color(c.Blended(col));
            const int N = 10;
            for (int i = 0; i < N; i++)
            {
                float u0 = i / (float)N, u1 = (i + 1) / (float)N;
                float w0 = r * Mathf.Sin((1f - u0) * Mathf.PI * 0.5f + 0.3f) * (1f - u0 * 0.9f), w1 = r * Mathf.Sin((1f - u1) * Mathf.PI * 0.5f + 0.3f) * (1f - u1 * 0.9f);
                float x0 = x + lean * l * u0 * u0 + Mathf.Sin(u0 * 7f + t * 9f + seed) * r * 0.15f * u0, x1 = x + lean * l * u1 * u1 + Mathf.Sin(u1 * 7f + t * 9f + seed) * r * 0.15f * u1;
                float y0 = y + l * u0, y1 = y + l * u1;
                GL.Vertex3(x0 - w0, y0, 0f); GL.Vertex3(x1 - w1, y1, 0f); GL.Vertex3(x1 + w1, y1, 0f);
                GL.Vertex3(x0 - w0, y0, 0f); GL.Vertex3(x1 + w1, y1, 0f); GL.Vertex3(x0 + w0, y0, 0f);
            }
            GL.End();
        }
    }

    /// <summary>Rain on a pond, seen from above: rings spreading and crossing, lily pads drifting.</summary>
    internal sealed class RipplesEffect : LoadingEffect
    {
        private const int Drops = 26, Pads = 7;

        public override string Name => "ripples";
        public override Color Background => new Color(0.05f, 0.12f, 0.13f, 1f);

        public override void Draw(EffectContext c)
        {
            float w = c.W, h = c.H;
            if (!c.Blend())
                return;
            GL.Begin(GL.QUADS);
            Gfx.RectV(0f, 0f, w, h, c.Blended(new Color(0.04f, 0.1f, 0.11f)), c.Blended(new Color(0.07f, 0.15f, 0.16f)));
            GL.End();
            // Each drop lands somewhere new every cycle and leaves three rings.
            GL.Begin(GL.LINES);
            for (int i = 0; i < Drops; i++)
            {
                float period = 3.2f + 1.6f * Noise.Hash(i, 1);
                float phase = c.T / period + Noise.Hash(i, 2);
                int cycle = Mathf.FloorToInt(phase);
                float age = (phase - cycle) * period;
                float x = Noise.Hash(i * 31 + cycle, 5) * w, y = Noise.Hash(i * 17 + cycle, 6) * h;
                for (int k = 0; k < 3; k++)
                {
                    float a = age - k * 0.35f;
                    if (a <= 0f)
                        continue;
                    float r = h * 0.06f * a;
                    float alpha = 0.35f * Mathf.Clamp01(1f - a / 2.6f) * (1f - k * 0.25f);
                    if (alpha <= 0.01f)
                        continue;
                    Gfx.Arc(x, y, r, r * 0.92f, 48, c.Blended(new Color(0.75f, 0.9f, 0.9f), alpha));
                }
            }
            GL.End();
            // Lily pads, slowly turning, a notch cut in each.
            for (int i = 0; i < Pads; i++)
            {
                float x = w * (0.1f + 0.8f * Noise.Hash(i, 51)) + Mathf.Sin(c.T * 0.05f + i) * h * 0.02f;
                float y = h * (0.1f + 0.8f * Noise.Hash(51, i)) + Mathf.Cos(c.T * 0.04f + i) * h * 0.02f;
                float r = h * (0.035f + 0.04f * Noise.Hash(i, 52)), rot = c.T * 0.03f * (i % 2 == 0 ? 1f : -1f) + i;
                Color pad = new Color(0.22f, 0.42f, 0.2f), padDark = new Color(0.16f, 0.32f, 0.15f);
                Gfx.Fan(x, y, r, r * 0.95f, 32, c.Blended(pad), c.Blended(padDark), rot + 0.12f, rot + Gfx.Tau - 0.06f);
                if (i % 3 == 0)
                    Gfx.Fan(x + r * 0.2f, y + r * 0.1f, r * 0.18f, r * 0.18f, 12, c.Blended(new Color(0.95f, 0.8f, 0.85f)), c.Blended(new Color(0.88f, 0.6f, 0.7f)));
            }
        }
    }

    /// <summary>A city at dusk whose windows light up one by one as the game loads.</summary>
    internal sealed class CityEffect : LoadingEffect
    {
        public override string Name => "city";

        public override void Draw(EffectContext c)
        {
            float w = c.W, h = c.H, p = c.Exiting ? 1f : c.Progress;
            if (!c.Blend())
                return;
            GL.Begin(GL.QUADS);
            Gfx.RectV(0f, h * 0.2f, w, h * 0.55f, c.Blended(new Color(0.55f, 0.38f, 0.4f)), c.Blended(new Color(0.2f, 0.2f, 0.32f)));
            Gfx.RectV(0f, h * 0.55f, w, h, c.Blended(new Color(0.2f, 0.2f, 0.32f)), c.Blended(new Color(0.06f, 0.07f, 0.13f)));
            GL.End();
            Gfx.Fan(w * 0.8f, h * 0.8f, h * 0.035f, h * 0.035f, 32, c.Blended(new Color(0.95f, 0.93f, 0.85f)), c.Blended(new Color(0.9f, 0.88f, 0.8f)));
            for (int layer = 0; layer < 3; layer++)
            {
                Color body = layer == 0 ? new Color(0.17f, 0.17f, 0.24f) : layer == 1 ? new Color(0.1f, 0.1f, 0.15f) : new Color(0.04f, 0.04f, 0.06f);
                float baseY = h * (0.2f - 0.07f * layer);
                float x = -w * 0.02f * layer;
                int id = 0;
                while (x < w)
                {
                    int seed = layer * 997 + id;
                    float bw = h * (0.06f + 0.09f * Noise.Hash(seed, 1));
                    float bh = h * (0.12f + (0.35f - 0.08f * layer) * Noise.Hash(seed, 2) * Noise.Hash(seed, 9));
                    GL.Begin(GL.QUADS);
                    Gfx.Rect(x, -10f, x + bw - h * 0.004f, baseY + bh, c.Blended(body));
                    // Windows: each switches on at its own point of the progress (and a few blink off again).
                    float ww = h * 0.008f, wh = h * 0.011f, gx = h * 0.016f, gy = h * 0.022f;
                    int cols = Mathf.Max(1, Mathf.FloorToInt((bw - h * 0.016f) / gx)), rows = Mathf.FloorToInt((bh - h * 0.02f) / gy);
                    for (int r = 0; r < rows; r++)
                        for (int k = 0; k < cols; k++)
                        {
                            float on = Noise.Hash(seed * 31 + r, k + 7);
                            if (on > p * 1.05f)
                                continue;
                            if (Noise.Value(c.T * 0.3f + k, r + seed) > 0.93f)
                                continue;
                            float wx = x + h * 0.01f + k * gx, wy = baseY + h * 0.01f + r * gy;
                            Color lit = Noise.Hash(k, seed + r) < 0.8f ? new Color(1f, 0.78f, 0.45f) : new Color(0.7f, 0.85f, 1f);
                            Gfx.Rect(wx, wy, wx + ww, wy + wh, c.Blended(lit, (0.55f + 0.4f * Noise.Hash(r, k + seed)) * (1f - 0.35f * layer)));
                        }
                    GL.End();
                    x += Mathf.Max(bw, 1f);   // a zero-height screen (minimized window) must not stall the loop
                    id++;
                }
            }
        }
    }

    /// <summary>A lighthouse on a headland at night: the beam sweeps over a calm sea.</summary>
    internal sealed class LighthouseEffect : LoadingEffect
    {
        public override string Name => "lighthouse";
        public override Color Background => new Color(0.03f, 0.04f, 0.07f, 1f);

        public override void Draw(EffectContext c)
        {
            float w = c.W, h = c.H, hz = h * 0.34f;
            float lx = w * 0.74f, ly = hz + h * 0.32f;
            float beam = c.T * 0.7f;
            float facing = Mathf.Cos(beam);          // +1: towards the right of the screen, -1: to the left
            float toward = Mathf.Sin(beam);          // >0: towards us
            if (!c.Blend())
                return;
            GL.Begin(GL.QUADS);
            Gfx.RectV(0f, hz, w, h, c.Blended(new Color(0.1f, 0.12f, 0.2f)), c.Blended(new Color(0.02f, 0.03f, 0.06f)));
            Gfx.RectV(0f, 0f, w, hz, c.Blended(new Color(0.02f, 0.03f, 0.05f)), c.Blended(new Color(0.06f, 0.08f, 0.13f)));
            for (int i = 0; i < 90; i++)
            {
                float x = Noise.Hash(i, 61) * w, y = hz + h * (0.15f + (0.85f - 0.15f) * Noise.Hash(61, i)), s = h * 0.0012f;
                Gfx.Rect(x - s, y - s, x + s, y + s, c.Blended(Color.white, 0.2f + 0.5f * Noise.Hash(i, 62)));
            }
            // Calm swell: thin lines drifting, shorter towards the horizon.
            for (int i = 0; i < 40; i++)
            {
                float d = i / 40f, y = hz - h * 0.01f - d * d * hz;
                float len = w * (0.02f + 0.08f * d), x = Mathf.Repeat(Noise.Hash(i, 71) * w + c.T * h * 0.01f * (0.5f + d), w + len) - len;
                Gfx.Rect(x, y, x + len, y + 1f + 1.5f * d, c.Blended(new Color(0.55f, 0.62f, 0.75f), 0.1f + 0.15f * d));
            }
            GL.End();
            // The beam: a long pale wedge, shortest when it points at us or away.
            float len2 = w * 0.75f * Mathf.Abs(facing) + w * 0.05f;
            float spread = h * (0.015f + 0.05f * Mathf.Abs(toward));
            float ex = lx + Mathf.Sign(facing) * len2;
            GL.Begin(GL.TRIANGLES);
            GL.Color(c.Blended(new Color(1f, 0.95f, 0.8f), 0.28f));
            GL.Vertex3(lx, ly, 0f);
            GL.Color(c.Blended(new Color(1f, 0.95f, 0.8f), 0f));
            GL.Vertex3(ex, ly + spread, 0f); GL.Vertex3(ex, ly - spread, 0f);
            GL.End();
            // Headland and tower.
            GL.Begin(GL.QUADS);
            Color rock = c.Blended(new Color(0.03f, 0.03f, 0.04f));
            Gfx.Quad(w * 0.6f, 0f, w * 0.66f, hz + h * 0.1f, w + 10f, hz + h * 0.14f, w + 10f, 0f, rock);
            float tw = h * 0.03f, tb = hz + h * 0.1f;
            Gfx.Quad(lx - tw, tb, lx - tw * 0.7f, ly - h * 0.02f, lx + tw * 0.7f, ly - h * 0.02f, lx + tw, tb, c.Blended(new Color(0.75f, 0.74f, 0.72f)));
            for (int b = 0; b < 2; b++)
            {
                float y0 = tb + (ly - tb) * (0.3f + 0.35f * b), y1 = y0 + (ly - tb) * 0.12f;
                Gfx.Rect(lx - tw * (0.9f - 0.15f * (0.3f + 0.35f * b)), y0, lx + tw * (0.9f - 0.15f * (0.3f + 0.35f * b)), y1, c.Blended(new Color(0.6f, 0.15f, 0.12f)));
            }
            Gfx.Rect(lx - tw * 0.8f, ly - h * 0.02f, lx + tw * 0.8f, ly + h * 0.025f, c.Blended(new Color(0.12f, 0.12f, 0.14f)));
            GL.End();
            float lamp = 0.6f + 0.4f * Mathf.Max(0f, toward);
            Gfx.Fan(lx, ly, h * 0.012f, h * 0.012f, 16, c.Blended(new Color(1f, 0.95f, 0.75f), lamp), c.Blended(new Color(1f, 0.9f, 0.6f), lamp * 0.5f));
            if (toward > 0.9f && c.Additive())
                Gfx.Fan(lx, ly, h * 0.1f, h * 0.1f, 24, c.Glow(new Color(1f, 0.95f, 0.8f), (toward - 0.9f) * 4f), Color.clear);
        }
    }
}
