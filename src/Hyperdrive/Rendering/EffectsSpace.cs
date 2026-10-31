using UnityEngine;

namespace Hyperdrive.Rendering
{
    /// <summary>Jool and its moons: a banded green giant, moons passing in front of and behind it.</summary>
    internal sealed class JoolEffect : LoadingEffect
    {
        private struct Moon
        {
            public float A, Size, Speed, Phase;
            public Color Col;
        }

        private static readonly Moon[] Moons =
        {
            new Moon { A = 0.38f, Size = 0.020f, Speed = 0.21f, Phase = 0.3f, Col = new Color(0.35f, 0.55f, 0.85f) },   // Laythe
            new Moon { A = 0.52f, Size = 0.016f, Speed = 0.13f, Phase = 2.1f, Col = new Color(0.82f, 0.86f, 0.9f) },    // Vall
            new Moon { A = 0.70f, Size = 0.026f, Speed = 0.085f, Phase = 4.0f, Col = new Color(0.62f, 0.6f, 0.58f) },   // Tylo
            new Moon { A = 0.86f, Size = 0.009f, Speed = 0.06f, Phase = 1.2f, Col = new Color(0.5f, 0.42f, 0.32f) },    // Bop
            new Moon { A = 0.98f, Size = 0.007f, Speed = 0.05f, Phase = 5.3f, Col = new Color(0.8f, 0.75f, 0.5f) },     // Pol
        };

        public override string Name => "jool";
        public override Color Background => new Color(0.004f, 0.005f, 0.01f, 1f);

        public override void Draw(EffectContext c)
        {
            float w = c.W, h = c.H, cx = w * 0.5f, cy = h * 0.5f, R = h * 0.22f;
            const float tilt = 0.18f;   // orbits seen nearly edge-on
            if (c.Additive())
            {
                GL.Begin(GL.QUADS);
                SceneBits.Stars(c, 220, 101, 0f, 0.45f);
                GL.End();
            }
            if (!c.Blend())
                return;
            DrawMoons(c, cx, cy, R, tilt, behind: true);
            // Bands: horizontal slices of the disc, shaded from the night side (left) to the day side (right).
            GL.Begin(GL.QUADS);
            const int Slices = 110;
            for (int j = 0; j < Slices; j++)
            {
                float y0 = cy - R + 2f * R * j / Slices, y1 = y0 + 2f * R / Slices;
                float ym = (y0 + y1) * 0.5f - cy;
                float half = Mathf.Sqrt(Mathf.Max(0f, R * R - ym * ym));
                float band = Noise.Fbm(j * 0.06f, c.T * 0.02f + 3f, 3);
                Color col = Gfx.Lerp(new Color(0.22f, 0.42f, 0.12f), new Color(0.55f, 0.75f, 0.28f), band * 1.4f - 0.2f);
                float limb = half / R;
                Color day = c.Blended(col * (0.55f + 0.45f * limb)), night = c.Blended(col * 0.08f);
                GL.Color(night); GL.Vertex3(cx - half, y0, 0f); GL.Vertex3(cx - half, y1, 0f);
                GL.Color(day); GL.Vertex3(cx + half * 0.35f, y1, 0f); GL.Vertex3(cx + half * 0.35f, y0, 0f);
                GL.Color(day); GL.Vertex3(cx + half * 0.35f, y0, 0f); GL.Vertex3(cx + half * 0.35f, y1, 0f);
                GL.Color(c.Blended(col * 0.75f)); GL.Vertex3(cx + half, y1, 0f); GL.Vertex3(cx + half, y0, 0f);
            }
            GL.End();
            GL.Begin(GL.LINES);
            for (int k = 0; k < Moons.Length; k++)
            {
                float a = Moons[k].A * h;
                Gfx.Arc(cx, cy, a, a * tilt, 160, c.Blended(new Color(0.7f, 0.75f, 0.85f), 0.07f), Mathf.PI, Gfx.Tau);
            }
            GL.End();
            DrawMoons(c, cx, cy, R, tilt, behind: false);
            if (c.Additive())
                Gfx.Ring(cx, cy, R, R * 1.05f, 96, c.Glow(new Color(0.55f, 0.8f, 0.35f), 0.25f), Color.clear, -1.2f, 1.2f);
        }

        private static void DrawMoons(EffectContext c, float cx, float cy, float R, float tilt, bool behind)
        {
            float h = c.H;
            for (int k = 0; k < Moons.Length; k++)
            {
                Moon m = Moons[k];
                float ang = m.Phase + c.T * m.Speed;
                bool isBehind = Mathf.Sin(ang) > 0f;
                if (isBehind != behind)
                    continue;
                float x = cx + Mathf.Cos(ang) * m.A * h, y = cy + Mathf.Sin(ang) * m.A * h * tilt;
                if (behind && Mathf.Abs(x - cx) < R && Mathf.Abs(y - cy) < R)
                    continue;   // hidden by the planet
                float r = m.Size * h;
                Gfx.Fan(x, y, r, r, 24, c.Blended(m.Col * 0.25f), c.Blended(m.Col * 0.25f), Mathf.PI * 0.5f, Mathf.PI * 1.5f);
                Gfx.Fan(x, y, r, r, 24, c.Blended(m.Col), c.Blended(m.Col * 0.7f), -Mathf.PI * 0.5f, Mathf.PI * 0.5f);
            }
        }
    }

    /// <summary>Easter egg: a pixel-art Kerbal crossing the screen on a rainbow, once in a while.</summary>
    internal static class NyanKerbal
    {
        // W helmet, G skin, E eye white, K black, O orange suit, '.' nothing.
        private static readonly string[] Sprite =
        {
            "....WWWWWW....",
            "..WWGGGGGGWW..",
            ".WGGGGGGGGGGW.",
            "WGGEEGGGGEEGGW",
            "WGGEKGGGGEKGGW",
            "WGGGGGGGGGGGGW",
            "WGGKGGGGGGKGGW",
            ".WGGKKKKKKGGW.",
            "..WWGGGGGGWW..",
            "...OOOOOOOO...",
            "..OOOWWWWOOO..",
            "..OOO....OOO..",
        };

        private static readonly Color[] Rainbow =
        {
            new Color(0.95f, 0.2f, 0.2f), new Color(1f, 0.6f, 0.1f), new Color(1f, 0.93f, 0.2f),
            new Color(0.3f, 0.85f, 0.3f), new Color(0.25f, 0.6f, 1f), new Color(0.55f, 0.3f, 0.9f),
        };

        public const float Duration = 7f;

        /// <summary>t: seconds since the crossing started (0..Duration). Blend pass already set.</summary>
        public static void Draw(EffectContext c, float t)
        {
            float w = c.W, h = c.H, px = Mathf.Round(h * 0.007f);
            float spriteW = 14 * px, spriteH = Sprite.Length * px;
            float x = -spriteW + (w + spriteW * 2f) * (t / Duration);
            float y = h * 0.64f + Mathf.Round(Mathf.Sin(t * 5f) * 2f) * px;
            GL.Begin(GL.QUADS);
            // Rainbow trail: six bands of stepped waves, one wave step every two sprite pixels.
            float bandH = spriteH * 0.5f / Rainbow.Length;
            float trailY = y + spriteH * 0.62f;
            for (float tx = x + px * 2f; tx > -px * 4f; tx -= px * 4f)
            {
                float step = (Mathf.Floor((tx - x) / (px * 4f) + t * 6f) % 2 == 0) ? px : 0f;
                for (int b = 0; b < Rainbow.Length; b++)
                {
                    float by = trailY - b * bandH + step;
                    Gfx.Rect(tx - px * 4f, by - bandH, tx, by, c.Blended(Rainbow[b], 0.9f));
                }
            }
            for (int row = 0; row < Sprite.Length; row++)
            {
                string line = Sprite[row];
                float ry = y + spriteH - (row + 1) * px;
                for (int col = 0; col < line.Length; col++)
                {
                    Color pc;
                    switch (line[col])
                    {
                        case 'W': pc = new Color(0.93f, 0.94f, 0.96f); break;
                        case 'G': pc = new Color(0.47f, 0.76f, 0.26f); break;
                        case 'E': pc = Color.white; break;
                        case 'K': pc = new Color(0.05f, 0.05f, 0.06f); break;
                        case 'O': pc = new Color(0.95f, 0.5f, 0.12f); break;
                        default: continue;
                    }
                    float rx = x + col * px;
                    Gfx.Rect(rx, ry, rx + px, ry + px, c.Blended(pc));
                }
            }
            // Twinkling pixel sparkles around.
            for (int i = 0; i < 10; i++)
            {
                float sx = x - spriteW * (1f + 3f * Noise.Hash(i, 1)), sy = y + spriteH * (Noise.Hash(i, 2) * 3f - 1f);
                if (Mathf.Repeat(t * 3f + i * 0.37f, 1f) < 0.5f)
                {
                    Gfx.Rect(sx, sy, sx + px, sy + px * 3f, c.Blended(Color.white, 0.8f));
                    Gfx.Rect(sx - px, sy + px, sx + px * 2f, sy + px * 2f, c.Blended(Color.white, 0.8f));
                }
            }
            GL.End();
        }
    }

    internal static class LoadingEffectCatalog
    {
        /// <summary>Every effect drawn by its own code (the four star-field styles live in WarpScreenView).</summary>
        public static LoadingEffect[] Create() => new LoadingEffect[]
        {
            new BlueprintEffect(), new SurveyEffect(), new NavballEffect(), new ChecklistEffect(), new LaunchPadEffect(), new MunDescentEffect(),
            new AscentEffect(), new KscNightEffect(), new AuroraEffect(), new PolarEffect(), new SplashdownEffect(), new ReentryEffect(),
            new StagingEffect(), new JoolEffect(),
            // Away from space: quiet scenes.
            new RainEffect(), new ForestEffect(), new CampfireEffect(), new RipplesEffect(), new CityEffect(), new LighthouseEffect(),
            // KSP jokes.
            new StrutsEffect(), new RudEffect(), new RoverEffect(), new BoostersEffect(), new KrakenEffect(), new BounceEffect(),
            // KSP memes.
            new FlipEffect(), new TwrEffect(), new ChuteEffect(), new RunwayEffect(), new DebrisEffect(),
        };
    }
}
