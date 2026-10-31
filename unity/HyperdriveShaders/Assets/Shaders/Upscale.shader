// Hyperdrive upscaler: AMD FidelityFX Super Resolution 1.0 (EASU upscaling + RCAS sharpening), ported to Unity's built-in
// pipeline for the KSP 1.12 renderer (OpenGL Core / Direct3D 11, no compute). Scalar (non-packed) path of ffx_fsr1.h,
// with explicit texel loads instead of gather4.
//
// Copyright (c) 2021 Advanced Micro Devices, Inc. All rights reserved.
//
// Permission is hereby granted, free of charge, to any person obtaining a copy of this software and associated
// documentation files (the "Software"), to deal in the Software without restriction, including without limitation the
// rights to use, copy, modify, merge, publish, distribute, sublicense, and/or sell copies of the Software, and to permit
// persons to whom the Software is furnished to do so, subject to the following conditions:
//
// The above copyright notice and this permission notice shall be included in all copies or substantial portions of the
// Software.
//
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE
// WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR
// COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR
// OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.
Shader "Hidden/Hyperdrive/Upscale"
{
    Properties
    {
        _MainTex ("Source", 2D) = "black" {}
    }
    SubShader
    {
        Cull Off ZWrite Off ZTest Always

        CGINCLUDE
        #include "UnityCG.cginc"

        sampler2D _MainTex;
        float4 _MainTex_TexelSize;   // source: 1/w, 1/h, w, h
        float4 _OutputSize;          // destination: w, h, 1/w, 1/h
        float _Sharpness;            // RCAS: exp2(-stops), 1 = sharpest

        struct v2f
        {
            float4 pos : SV_POSITION;
            float2 uv : TEXCOORD0;
        };

        v2f vert(appdata_img v)
        {
            v2f o;
            o.pos = UnityObjectToClipPos(v.vertex);
            o.uv = v.texcoord;
            return o;
        }

        // Texel at integer pixel index p (centers at p + 0.5), no filtering in effect.
        float3 Load(float2 p)
        {
            return tex2Dlod(_MainTex, float4((p + 0.5) * _MainTex_TexelSize.xy, 0, 0)).rgb;
        }

        // Luma times 2, as FSR.
        float Luma(float3 c)
        {
            return c.b * 0.5 + (c.r * 0.5 + c.g);
        }

        void EasuSet(inout float2 dir, inout float len, float w, float lA, float lB, float lC, float lD, float lE)
        {
            float dc = lD - lC;
            float cb = lC - lB;
            float lenX = 1.0 / max(max(abs(dc), abs(cb)), 1e-8);
            float dirX = lD - lB;
            dir.x += dirX * w;
            lenX = saturate(abs(dirX) * lenX);
            lenX *= lenX;
            len += lenX * w;
            float ec = lE - lC;
            float ca = lC - lA;
            float lenY = 1.0 / max(max(abs(ec), abs(ca)), 1e-8);
            float dirY = lE - lA;
            dir.y += dirY * w;
            lenY = saturate(abs(dirY) * lenY);
            lenY *= lenY;
            len += lenY * w;
        }

        void EasuTap(inout float3 aC, inout float aW, float2 off, float2 dir, float2 len, float lob, float clp, float3 c)
        {
            float2 v;
            v.x = off.x * dir.x + off.y * dir.y;
            v.y = off.x * -dir.y + off.y * dir.x;
            v *= len;
            float d2 = min(v.x * v.x + v.y * v.y, clp);
            float wB = (2.0 / 5.0) * d2 - 1.0;
            float wA = lob * d2 - 1.0;
            wB *= wB;
            wA *= wA;
            wB = (25.0 / 16.0) * wB - (25.0 / 16.0 - 1.0);
            float w = wB * wA;
            aC += c * w;
            aW += w;
        }

        float4 FragEasu(v2f i) : SV_Target
        {
            float2 ip = floor(i.uv * _OutputSize.xy);
            float2 pp = (ip + 0.5) * (_MainTex_TexelSize.zw * _OutputSize.zw) - 0.5;
            float2 fp = floor(pp);
            pp -= fp;

            //    b c
            //  e f g h
            //  i j k l
            //    n o
            float3 b = Load(fp + float2(0, -1));
            float3 c = Load(fp + float2(1, -1));
            float3 e = Load(fp + float2(-1, 0));
            float3 f = Load(fp);
            float3 g = Load(fp + float2(1, 0));
            float3 h = Load(fp + float2(2, 0));
            float3 ii = Load(fp + float2(-1, 1));
            float3 j = Load(fp + float2(0, 1));
            float3 k = Load(fp + float2(1, 1));
            float3 l = Load(fp + float2(2, 1));
            float3 n = Load(fp + float2(0, 2));
            float3 o = Load(fp + float2(1, 2));

            float bL = Luma(b), cL = Luma(c), eL = Luma(e), fL = Luma(f), gL = Luma(g), hL = Luma(h);
            float iL = Luma(ii), jL = Luma(j), kL = Luma(k), lL = Luma(l), nL = Luma(n), oL = Luma(o);

            float2 dir = 0;
            float len = 0;
            EasuSet(dir, len, (1.0 - pp.x) * (1.0 - pp.y), bL, eL, fL, gL, jL);
            EasuSet(dir, len, pp.x * (1.0 - pp.y), cL, fL, gL, hL, kL);
            EasuSet(dir, len, (1.0 - pp.x) * pp.y, fL, iL, jL, kL, nL);
            EasuSet(dir, len, pp.x * pp.y, gL, jL, kL, lL, oL);

            float2 dir2 = dir * dir;
            float dirR = dir2.x + dir2.y;
            bool zro = dirR < (1.0 / 32768.0);
            dirR = zro ? 1.0 : rsqrt(dirR);
            dir.x = zro ? 1.0 : dir.x;
            dir *= dirR;
            len = len * 0.5;
            len *= len;
            float stretch = (dir.x * dir.x + dir.y * dir.y) / max(max(abs(dir.x), abs(dir.y)), 1e-8);
            float2 len2 = float2(1.0 + (stretch - 1.0) * len, 1.0 - 0.5 * len);
            float lob = 0.5 + ((1.0 / 4.0 - 0.04) - 0.5) * len;
            float clp = 1.0 / lob;

            float3 min4 = min(min(f, g), min(j, k));
            float3 max4 = max(max(f, g), max(j, k));

            float3 aC = 0;
            float aW = 0;
            EasuTap(aC, aW, float2(0, -1) - pp, dir, len2, lob, clp, b);
            EasuTap(aC, aW, float2(1, -1) - pp, dir, len2, lob, clp, c);
            EasuTap(aC, aW, float2(-1, 1) - pp, dir, len2, lob, clp, ii);
            EasuTap(aC, aW, float2(0, 1) - pp, dir, len2, lob, clp, j);
            EasuTap(aC, aW, float2(0, 0) - pp, dir, len2, lob, clp, f);
            EasuTap(aC, aW, float2(-1, 0) - pp, dir, len2, lob, clp, e);
            EasuTap(aC, aW, float2(1, 1) - pp, dir, len2, lob, clp, k);
            EasuTap(aC, aW, float2(2, 1) - pp, dir, len2, lob, clp, l);
            EasuTap(aC, aW, float2(2, 0) - pp, dir, len2, lob, clp, h);
            EasuTap(aC, aW, float2(1, 0) - pp, dir, len2, lob, clp, g);
            EasuTap(aC, aW, float2(1, 2) - pp, dir, len2, lob, clp, o);
            EasuTap(aC, aW, float2(0, 2) - pp, dir, len2, lob, clp, n);

            float3 pix = min(max4, max(min4, aC / aW));
            return float4(pix, 1);
        }

        #define FSR_RCAS_LIMIT (0.25 - (1.0 / 16.0))

        float4 FragRcas(v2f i) : SV_Target
        {
            float2 sp = floor(i.uv * _MainTex_TexelSize.zw);
            //    b
            //  d e f
            //    h
            float3 b = Load(sp + float2(0, -1));
            float3 d = Load(sp + float2(-1, 0));
            float3 e = Load(sp);
            float3 f = Load(sp + float2(1, 0));
            float3 h = Load(sp + float2(0, 1));

            float3 mn4 = min(min(b, d), min(f, h));
            float3 mx4 = max(max(b, d), max(f, h));
            float3 hitMin = min(mn4, e) / max(4.0 * mx4, 1e-5);
            float3 hitMax = (1.0 - max(mx4, e)) / min(4.0 * mn4 - 4.0, -1e-5);
            float3 lobeRGB = max(-hitMin, hitMax);
            float lobe = max(-FSR_RCAS_LIMIT, min(max(lobeRGB.r, max(lobeRGB.g, lobeRGB.b)), 0.0)) * _Sharpness;
            float rcpL = 1.0 / (4.0 * lobe + 1.0);
            float3 pix = (lobe * (b + d + h + f) + e) * rcpL;
            return float4(pix, 1);
        }
        ENDCG

        // 0: EASU, source (rendered size) to destination (display size)
        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment FragEasu
            #pragma target 3.0
            ENDCG
        }

        // 1: RCAS, same size
        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment FragRcas
            #pragma target 3.0
            ENDCG
        }
    }
    Fallback Off
}
