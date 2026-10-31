using System;
using Hyperdrive.Core;
using UnityEngine;

namespace Hyperdrive.Rendering
{
    /// <summary>
    /// What a loading effect knows about the frame. Energy follows the animation's phases (0 at the start, 1 while the
    /// game loads, fading out on approach and exit); Progress is the loading progress shown on screen; Overlay is the
    /// cross-fade to the main menu (1 = fully covering). Colours handed to the GL helpers go through Blended (alpha
    /// blending: overlay scales alpha) or Glow (additive: overlay scales the colour).
    /// </summary>
    internal sealed class EffectContext
    {
        public float W, H, T, Dt, Energy, Progress, Overlay = 1f, Flash;
        public bool Exiting;

        /// <summary>A text in the player's language (Core.Lang, Localization/*.cfg): a lookup, nothing allocated.</summary>
        public string Tr(string key) => Lang.T(key);
        public Material BlendMaterial, AdditiveMaterial;

        public bool Blend() => BlendMaterial != null && BlendMaterial.SetPass(0);

        public bool Additive() => AdditiveMaterial != null && AdditiveMaterial.SetPass(0);

        public Color Blended(Color c) => new Color(c.r, c.g, c.b, c.a * Overlay);

        public Color Blended(Color c, float alpha) => new Color(c.r, c.g, c.b, alpha * Overlay);

        public Color Glow(Color c, float k = 1f)
        {
            float m = k * c.a * Overlay;
            return new Color(c.r * m, c.g * m, c.b * m, 1f);
        }

        /// <summary>GUI style the view prepares before DrawGui (KSP's Calibri).</summary>
        public GUIStyle Text;

        /// <summary>Text whose top edge is at height y (pixels from the bottom, like the GL drawing); x is its left edge, or its right edge when alignRight.</summary>
        public void Label(float x, float y, string text, Color color, float size, bool alignRight = false, FontStyle style = FontStyle.Normal)
        {
            if (Text == null)
                return;
            Text.fontStyle = style;
            Text.fontSize = Mathf.Max(8, Mathf.RoundToInt(size));
            Text.alignment = alignRight ? TextAnchor.UpperRight : TextAnchor.UpperLeft;
            GUI.color = new Color(color.r, color.g, color.b, color.a * Overlay);
            float top = H - y;
            GUI.Label(alignRight ? new Rect(x - 1400f, top, 1400f, size * 1.7f) : new Rect(x, top, 1400f, size * 1.7f), text, Text);
        }
    }

    /// <summary>One loading screen effect. Allocates in Init only; Step and Draw run every frame without allocating.</summary>
    internal abstract class LoadingEffect
    {
        public abstract string Name { get; }

        public virtual Color Background => new Color(0.008f, 0.008f, 0.012f, 1f);

        /// <summary>Hand-drawn wobble of the shapes, in pixels at 1080p (0 = steady vector shapes).</summary>
        public virtual float Boil => 0f;

        /// <summary>Texture finish drawn over the effect (grain, paper, cinema frame).</summary>
        public virtual Finish Finish => Finish.Film;

        private string _exit;

        /// <summary>The caption shown while the screen fades out (#HD_Exit_&lt;Name&gt;, or the default one), cached.</summary>
        public string ExitText
        {
            get
            {
                if (_exit == null)
                {
                    string key = "#HD_Exit_" + Name;
                    _exit = Lang.T(key);
                    if (_exit == key)
                        _exit = Lang.T("#HD_Exit_default");
                }
                return _exit;
            }
        }


        public virtual void Init(EffectContext c, System.Random rng) { }

        public virtual void Step(EffectContext c) { }

        public abstract void Draw(EffectContext c);

        /// <summary>Text drawn with the GUI after the shapes (OnGUI repaint).</summary>
        public virtual void DrawGui(EffectContext c) { }

        protected static float Rand(System.Random rng) => (float)rng.NextDouble();

        protected static float Rand(System.Random rng, float min, float max) => min + (max - min) * (float)rng.NextDouble();
    }

    /// <summary>GL immediate-mode helpers in pixel coordinates (origin bottom-left). Callers open GL.LINES/QUADS where noted.</summary>
    internal static class Gfx
    {
        public const float Tau = Mathf.PI * 2f;

        /// <summary>
        /// Hand-drawn wobble: while Boil &gt; 0 every vertex is moved by up to Boil pixels, redrawn BoilStep times a
        /// second like cel animation. The offset depends only on where the vertex is, so shared corners stay shared.
        /// </summary>
        public static float Boil;
        public static int BoilStep;

        public static void V(float x, float y)
        {
            if (Boil > 0f)
            {
                int qx = Mathf.FloorToInt(x * 0.11f), qy = Mathf.FloorToInt(y * 0.11f);
                x += (Noise.Hash(qx * 31 + BoilStep, qy) - 0.5f) * 2f * Boil;
                y += (Noise.Hash(qy * 17 - BoilStep * 7, qx + 5) - 0.5f) * 2f * Boil;
            }
            GL.Vertex3(x, y, 0f);
        }

        // Inside GL.LINES.
        public static void Line(float x0, float y0, float x1, float y1, Color c)
        {
            GL.Color(c); V(x0, y0); V(x1, y1);
        }

        public static void Line(float x0, float y0, Color c0, float x1, float y1, Color c1)
        {
            GL.Color(c0); V(x0, y0); GL.Color(c1); V(x1, y1);
        }

        // Inside GL.LINES: outline of an ellipse arc.
        public static void Arc(float cx, float cy, float rx, float ry, int segments, Color c, float a0 = 0f, float a1 = Tau)
        {
            GL.Color(c);
            float step = (a1 - a0) / segments;
            float px = cx + rx * Mathf.Cos(a0), py = cy + ry * Mathf.Sin(a0);
            for (int s = 1; s <= segments; s++)
            {
                float a = a0 + step * s;
                float nx = cx + rx * Mathf.Cos(a), ny = cy + ry * Mathf.Sin(a);
                V(px, py); V(nx, ny);
                px = nx; py = ny;
            }
        }

        // Inside GL.QUADS.
        public static void Rect(float x0, float y0, float x1, float y1, Color c)
        {
            GL.Color(c);
            V(x0, y0); V(x0, y1); V(x1, y1); V(x1, y0);
        }

        // Inside GL.QUADS: vertical gradient.
        public static void RectV(float x0, float y0, float x1, float y1, Color bottom, Color top)
        {
            GL.Color(bottom); V(x0, y0);
            GL.Color(top); V(x0, y1); V(x1, y1);
            GL.Color(bottom); V(x1, y0);
        }

        // Inside GL.QUADS: horizontal gradient.
        public static void RectH(float x0, float y0, float x1, float y1, Color left, Color right)
        {
            GL.Color(left); V(x0, y0); V(x0, y1);
            GL.Color(right); V(x1, y1); V(x1, y0);
        }

        // Inside GL.QUADS: any quadrilateral, corners in order.
        public static void Quad(float ax, float ay, float bx, float by, float cx, float cy, float dx, float dy, Color c)
        {
            GL.Color(c);
            V(ax, ay); V(bx, by); V(cx, cy); V(dx, dy);
        }

        // Inside GL.QUADS: a segment with a width.
        public static void Thick(float x0, float y0, float x1, float y1, float width, Color c)
        {
            float dx = x1 - x0, dy = y1 - y0, len = Mathf.Sqrt(dx * dx + dy * dy);
            if (len < 1e-4f)
                return;
            float nx = -dy / len * width * 0.5f, ny = dx / len * width * 0.5f;
            GL.Color(c);
            V(x0 + nx, y0 + ny); V(x1 + nx, y1 + ny);
            V(x1 - nx, y1 - ny); V(x0 - nx, y0 - ny);
        }

        // Own GL.TRIANGLES: filled ellipse (or sector) with a radial gradient.
        public static void Fan(float cx, float cy, float rx, float ry, int segments, Color centre, Color edge, float a0 = 0f, float a1 = Tau)
        {
            GL.Begin(GL.TRIANGLES);
            float step = (a1 - a0) / segments;
            float px = cx + rx * Mathf.Cos(a0), py = cy + ry * Mathf.Sin(a0);
            for (int s = 1; s <= segments; s++)
            {
                float a = a0 + step * s;
                float nx = cx + rx * Mathf.Cos(a), ny = cy + ry * Mathf.Sin(a);
                GL.Color(centre); V(cx, cy);
                GL.Color(edge); V(px, py); V(nx, ny);
                px = nx; py = ny;
            }
            GL.End();
        }

        // Own GL.TRIANGLES: annulus (or arc of it) from colour 'inner' to 'outer'.
        public static void Ring(float cx, float cy, float r0, float r1, int segments, Color inner, Color outer, float a0 = 0f, float a1 = Tau)
        {
            GL.Begin(GL.TRIANGLES);
            float step = (a1 - a0) / segments;
            for (int s = 0; s < segments; s++)
            {
                float aA = a0 + step * s, aB = aA + step;
                float cA = Mathf.Cos(aA), sA = Mathf.Sin(aA), cB = Mathf.Cos(aB), sB = Mathf.Sin(aB);
                GL.Color(inner); V(cx + r0 * cA, cy + r0 * sA);
                GL.Color(outer); V(cx + r1 * cA, cy + r1 * sA); V(cx + r1 * cB, cy + r1 * sB);
                GL.Color(inner); V(cx + r0 * cA, cy + r0 * sA);
                GL.Color(outer); V(cx + r1 * cB, cy + r1 * sB);
                GL.Color(inner); V(cx + r0 * cB, cy + r0 * sB);
            }
            GL.End();
        }

        // Inside GL.QUADS: a gradient printed in a few flat bands, like a flat-colour illustration.
        public static void Bands(float x0, float y0, float x1, float y1, Color bottom, Color top, int bands)
        {
            for (int i = 0; i < bands; i++)
                Rect(x0, Mathf.Lerp(y0, y1, (float)i / bands), x1, Mathf.Lerp(y0, y1, (float)(i + 1) / bands), Color.Lerp(bottom, top, (i + 0.5f) / bands));
        }

        public static Color Lerp(Color a, Color b, float t) => Color.LerpUnclamped(a, b, Mathf.Clamp01(t));
    }

    /// <summary>Deterministic value noise (no allocation).</summary>
    internal static class Noise
    {
        public static float Hash(int x, int y)
        {
            unchecked
            {
                int h = x * 374761393 + y * 668265263;
                h = (h ^ (h >> 13)) * 1274126177;
                return ((h ^ (h >> 16)) & 0x7fffffff) / 2147483647f;
            }
        }

        public static float Value(float x, float y)
        {
            int xi = Mathf.FloorToInt(x), yi = Mathf.FloorToInt(y);
            float fx = x - xi, fy = y - yi;
            fx = fx * fx * (3f - 2f * fx);
            fy = fy * fy * (3f - 2f * fy);
            float a = Hash(xi, yi), b = Hash(xi + 1, yi), c = Hash(xi, yi + 1), d = Hash(xi + 1, yi + 1);
            return Mathf.Lerp(Mathf.Lerp(a, b, fx), Mathf.Lerp(c, d, fx), fy);
        }

        public static float Fbm(float x, float y, int octaves)
        {
            float sum = 0f, amp = 0.5f, norm = 0f;
            for (int o = 0; o < octaves; o++)
            {
                sum += Value(x, y) * amp;
                norm += amp;
                x = x * 2.03f + 17.1f;
                y = y * 2.03f - 9.7f;
                amp *= 0.5f;
            }
            return sum / norm;
        }
    }
}
