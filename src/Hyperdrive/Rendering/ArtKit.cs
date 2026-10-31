using UnityEngine;

namespace Hyperdrive.Rendering
{
    /// <summary>How an effect is finished once drawn: film grain and vignette, paper, or a cinema frame.</summary>
    internal enum Finish { None, Film, Paper, Cinema }

    /// <summary>Texture finishes, generated once: film grain, paper, vignette. Drawn with a textured UI material.</summary>
    internal static class Finishes
    {
        private static Texture2D s_grain, s_paper, s_vignette;
        private static Material s_material;
        private static bool s_failed;

        private static bool Ready()
        {
            if (s_material != null)
                return true;
            if (s_failed)
                return false;
            Shader shader = Shader.Find("UI/Default") ?? Shader.Find("Sprites/Default");
            if (shader == null)
            {
                s_failed = true;
                return false;
            }
            s_material = new Material(shader) { hideFlags = HideFlags.HideAndDontSave };
            var rng = new System.Random(4242);

            s_grain = NewTexture(256, TextureWrapMode.Repeat, FilterMode.Point);
            var px = new Color32[256 * 256];
            for (int i = 0; i < px.Length; i++)
            {
                byte v = (byte)(rng.Next(256));
                px[i] = new Color32(v, v, v, 255);
            }
            s_grain.SetPixels32(px);
            s_grain.Apply(false, true);

            // Paper: soft blotches plus fine fibres running mostly one way.
            s_paper = NewTexture(256, TextureWrapMode.Repeat, FilterMode.Bilinear);
            for (int y = 0; y < 256; y++)
                for (int x = 0; x < 256; x++)
                {
                    float blot = Noise.Fbm(x * 0.03f, y * 0.03f, 4);
                    float fibre = Noise.Value(x * 0.6f, y * 0.06f);
                    float v = 0.5f + 0.35f * (blot - 0.5f) + 0.25f * (fibre - 0.5f) + 0.08f * ((float)rng.NextDouble() - 0.5f);
                    byte b = (byte)Mathf.Clamp(v * 255f, 0f, 255f);
                    px[y * 256 + x] = new Color32(b, b, b, 255);
                }
            s_paper.SetPixels32(px);
            s_paper.Apply(false, true);

            s_vignette = NewTexture(256, TextureWrapMode.Clamp, FilterMode.Bilinear);
            for (int y = 0; y < 256; y++)
                for (int x = 0; x < 256; x++)
                {
                    float dx = (x - 127.5f) / 127.5f, dy = (y - 127.5f) / 127.5f;
                    float d = Mathf.Sqrt(dx * dx * 0.85f + dy * dy);
                    float k = Mathf.Clamp01((d - 0.6f) / 0.85f);
                    float a = k * k * (3f - 2f * k);        // smoothstep: clear centre, darker towards the corners
                    px[y * 256 + x] = new Color32(0, 0, 0, (byte)(a * 255f));
                }
            s_vignette.SetPixels32(px);
            s_vignette.Apply(false, true);
            return true;
        }

        private static Texture2D NewTexture(int size, TextureWrapMode wrap, FilterMode filter) =>
            new Texture2D(size, size, TextureFormat.RGBA32, false) { wrapMode = wrap, filterMode = filter, hideFlags = HideFlags.HideAndDontSave };

        public static void Apply(EffectContext c, Finish finish)
        {
            if (finish == Finish.None || !Ready())
                return;
            float w = c.W, h = c.H;
            switch (finish)
            {
                case Finish.Film:
                    Textured(s_vignette, 0f, 0f, w, h, 0f, 0f, 1f, 1f, new Color(1f, 1f, 1f, 0.4f * c.Overlay));
                    Grain(c, 0.045f);
                    break;
                case Finish.Paper:
                    Textured(s_paper, 0f, 0f, w, h, 0f, 0f, w / 512f, h / 512f, new Color(1f, 0.96f, 0.86f, 0.16f * c.Overlay));
                    Textured(s_vignette, 0f, 0f, w, h, 0f, 0f, 1f, 1f, new Color(0.25f, 0.18f, 0.1f, 0.35f * c.Overlay));
                    Grain(c, 0.035f);
                    break;
                case Finish.Cinema:
                    Textured(s_vignette, 0f, 0f, w, h, 0f, 0f, 1f, 1f, new Color(1f, 1f, 1f, 0.7f * c.Overlay));
                    Grain(c, 0.08f);
                    if (c.BlendMaterial != null && c.BlendMaterial.SetPass(0))
                    {
                        // 2.39:1 frame.
                        float bar = Mathf.Max(0f, (h - w / 2.39f) * 0.5f);
                        GL.Begin(GL.QUADS);
                        Gfx.Rect(0f, 0f, w, bar, new Color(0f, 0f, 0f, c.Overlay));
                        Gfx.Rect(0f, h - bar, w, h, new Color(0f, 0f, 0f, c.Overlay));
                        GL.End();
                    }
                    break;
            }
        }

        // Grain moves to a new random offset every frame (24 times a second), like film.
        private static void Grain(EffectContext c, float strength)
        {
            float f = Mathf.Floor(c.T * 24f);
            float ou = Noise.Hash((int)f, 11), ov = Noise.Hash((int)f, 29);
            Textured(s_grain, 0f, 0f, c.W, c.H, ou, ov, ou + c.W / 256f / 1.5f, ov + c.H / 256f / 1.5f, new Color(1f, 1f, 1f, strength * c.Overlay));
        }

        private static void Textured(Texture2D tex, float x0, float y0, float x1, float y1, float u0, float v0, float u1, float v1, Color col)
        {
            s_material.mainTexture = tex;
            if (!s_material.SetPass(0))
                return;
            GL.Begin(GL.QUADS);
            GL.Color(col);
            GL.TexCoord2(u0, v0); GL.Vertex3(x0, y0, 0f);
            GL.TexCoord2(u0, v1); GL.Vertex3(x0, y1, 0f);
            GL.TexCoord2(u1, v1); GL.Vertex3(x1, y1, 0f);
            GL.TexCoord2(u1, v0); GL.Vertex3(x1, y0, 0f);
            GL.End();
        }
    }
}
