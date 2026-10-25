// SmoothRunning/Blend: made from tools/shaderpack/blend.glsl by tools/unityshaders/port.py. Do not edit: edit the GLSL and run that again.
Shader "SmoothRunning/Blend"
{
    Properties
    {

    }
    SubShader
    {
        Tags { "Queue" = "Transparent" "RenderType" = "Transparent" "IgnoreProjector" = "True" }
        Pass
        {
            Blend One Zero
            ZWrite Off
            ZTest Always
            Cull Off

            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 4.5
            #pragma only_renderers d3d11 glcore metal vulkan
            #include "UnityCG.cginc"

            // (the GLSL has each of the game's matrices as its four columns; HLSL has them as rows)
            #define hlslcc_mtx4x4unity_ObjectToWorld transpose(unity_ObjectToWorld)
            #define hlslcc_mtx4x4unity_WorldToObject transpose(unity_WorldToObject)
            #define hlslcc_mtx4x4unity_MatrixVP transpose(UNITY_MATRIX_VP)
            #define hlslcc_mtx4x4unity_MatrixV transpose(UNITY_MATRIX_V)
            #define SampleLod(S, uv, lod) S.SampleLevel(sampler##S, uv, lod)
            #define SampleFlat(S, uv) S.SampleLevel(sampler##S, uv, 0.0)
            #define SampleGradient(S, uv, dx, dy) S.SampleGrad(sampler##S, uv, dx, dy)
            #define Mod(x, y) ((x) - (y) * floor((x) / (y)))
            #define GreaterThan(a, b) ((a) > (b))
            #define GreaterThanEqual(a, b) ((a) >= (b))
            #define LessThan(a, b) ((a) < (b))
            #define LessThanEqual(a, b) ((a) <= (b))
            float4 TexelFetch(Texture2D t, int2 p, int lod) { return t.Load(int3(p, lod)); }
            float4 TexelFetch(Texture3D t, int3 p, int lod) { return t.Load(int4(p, lod)); }
            int2 TextureSize(Texture2D t, int lod) { uint w, h; t.GetDimensions(w, h); return int2(w, h); }
            bool AllZero(float4 v) { return !any(v); }
            // (one of a vector's numbers, chosen by a variable, read and given a new value: see chosen in tools/unityshaders/port.py)
            float Pick(float2 v, int n) { return n <= 0 ? v.x : v.y; }
            float Pick(float3 v, int n) { return n <= 0 ? v.x : n == 1 ? v.y : v.z; }
            float Pick(float4 v, int n) { return n <= 0 ? v.x : n == 1 ? v.y : n == 2 ? v.z : v.w; }
            float2 Put(float2 v, int n, float x) { return float2(n == 0 ? x : v.x, n == 1 ? x : v.y); }
            float3 Put(float3 v, int n, float x) { return float3(n == 0 ? x : v.x, n == 1 ? x : v.y, n == 2 ? x : v.z); }
            float4 Put(float4 v, int n, float x) { return float4(n == 0 ? x : v.x, n == 1 ? x : v.y, n == 2 ? x : v.z, n == 3 ? x : v.w); }
            // gl_FragCoord: which pixel is being drawn, its rows counted from the bottom, as every picture's are read here. Direct3D counts
            // the rows of what it draws into from the top, and the game draws into its pictures upside down to make up for it (saying so with
            // _ProjectionParams.x at -1): so where it is drawing straight to the screen, the row is turned over, or each picture read by pixel
            // (the camera's depth, the smoke drawn small) would be read upside down.
            float4 FragCoord(float4 pos)
            {
            #if UNITY_UV_STARTS_AT_TOP
                if (_ProjectionParams.x > 0.0) pos.y = _ScreenParams.y - pos.y;
            #endif
                return pos;
            }

            float4 _BlendRight;     // xyz: the camera's right, times the tangent of half its width of view; w: 1 if its depth picture can be used
            float4 _BlendUp;     // xyz: its up, times the tangent of half its height of view; w: a number that changes every frame
            float4 _BlendAhead;     // xyz: the way it looks (a unit); w: 1 if depths run backwards (nearest is 1)
            float4 _BlendPrevRight;     // xyz: the camera's right at the last frame, in this frame's axes as the planet turned them (a unit); w: the tangent of half its width of view then
            float4 _BlendPrevUp;     // xyz: its up then (a unit); w: the tangent of half its height of view then
            float4 _BlendPrevAhead;     // xyz: the way it looked then (a unit); w: the picture's height in pixels
            float4 _BlendMoved;     // xyz: how far the camera moved against the planet since the last frame (m, this frame's axes); w: the far ground's distance from the planet's centre (m)
            float4 _BlendCam;     // xyz: the camera, from the planet's centre (m); w: 1 where the planet has ground to meet
            float4 _BlendShutter;     // x: the part of the motion since the last frame that is blended (the shutter's share of a frame); y: how many looks along it; z: the longest blend, as a part of the picture's height; w: the picture's width over its height
            float4 _BlendAC;     // a craft: xyz the middle of its box, from the camera (m); w 1 if there is one
            float4 _BlendAX;     // xyz: the box's first axis, over its half-length (so a point inside lies within -1 to 1 along each)
            float4 _BlendAY;
            float4 _BlendAZ;
            float4 _BlendAPivot;     // xyz: the point the craft turned about (its root part), from the camera (m)
            float4 _BlendAMove;     // xyz: how far that point moved against the planet since the last frame (m, this frame's axes)
            float4 _BlendASpin;     // xyz: how far the craft turned since the last frame (an axis, times the angle in radians)
            float4 _BlendBC;     // a second craft, the same
            float4 _BlendBX;
            float4 _BlendBY;
            float4 _BlendBZ;
            float4 _BlendBPivot;
            float4 _BlendBMove;
            float4 _BlendBSpin;
            Texture2D _BlendScene; SamplerState sampler_BlendScene;     // the picture so far
            Texture2D _CameraDepthTexture; SamplerState sampler_CameraDepthTexture;     // how far off each of its points is, for the near camera

            struct v2f
            {
                float4 pos : SV_POSITION;
                float2 vs_TEXCOORD0 : TEXCOORD0;
            };

            // Smooth Running: frame blending (Blend.cs). One triangle over the whole picture.

            v2f vert(float3 in_POSITION0 : POSITION)
            {
                v2f OUT = (v2f)0;
                OUT.pos = float4(in_POSITION0.xy, 0.5, 1.0);
                // (drawn into whatever the camera is drawing into, which may lie either way: as for the lens's second go, above; Natural
                // Light's veil and the sea's lens in the water the same; and Cinema Camera's look, whose picture came out upside down on
                // Direct3D without it, the camera drawing into a picture of its own there (the PC, 2026-10-20))
                float way = 1.0;
#if UNITY_UV_STARTS_AT_TOP
                way = _ProjectionParams.x;
#endif
                OUT.vs_TEXCOORD0 = float2(in_POSITION0.x * 0.5 + 0.5, in_POSITION0.y * way * 0.5 + 0.5);
                return OUT;
            }

            // Each frame blended along the way each thing in it moved while it was being "exposed", as a film camera's shutter blends
            // it: at a low frame rate the jumps between frames read as motion, not as a string of stills. What moved is reckoned for
            // each pixel from where it stands (the near camera's depth picture; beyond it the planet's own round ground, or the sky,
            // infinitely far) and from how the camera, and the craft it shows, moved against the planet since the last frame. The
            // craft the camera follows moves with it, and stays sharp; the ground streams past it.

            float chance(float2 p) { return frac(sin(dot(p, float2(12.9898, 78.233))) * 43758.5453); }

            // How far ahead of the camera a pixel's point lies (along the way it looks), or a negative number where the depth picture
            // has nothing there (the far camera's ground, the sky).
            float ahead(float2 uv)
            {
                float raw = SampleLod(_CameraDepthTexture, uv, 0.0).r;
                bool none = _BlendAhead.w > 0.5 ? raw <= 1e-6 : raw >= 0.999999;
                if (none) return -1.0;
                return 1.0 / (_ZBufferParams.z * raw + _ZBufferParams.w);
            }

            // Whether a point (from the camera) is inside a craft's box.
            bool inside(float3 p, float4 c, float4 x, float4 y, float4 z)
            {
                if (c.w < 0.5) return false;
                float3 d = p - c.xyz;
                return abs(dot(d, x.xyz)) <= 1.0 && abs(dot(d, y.xyz)) <= 1.0 && abs(dot(d, z.xyz)) <= 1.0;
            }

            // Where a pixel's point was at the last frame, from the camera as it stood then (this frame's axes); w 0 if infinitely far.
            float4 was(float2 uv, float depth)
            {
                float2 s = uv * 2.0 - 1.0;
                float3 ray = _BlendAhead.xyz + s.x * _BlendRight.xyz + s.y * _BlendUp.xyz;
                float3 p;
                if (depth > 0.0) p = ray * depth;
                else
                {
                    // beyond the near camera: the planet's round ground where the line of sight meets it, else the sky
                    float3 d = normalize(ray);
                    float b = dot(_BlendCam.xyz, d), c = dot(_BlendCam.xyz, _BlendCam.xyz) - _BlendMoved.w * _BlendMoved.w;
                    float disc = b * b - c;
                    float t = disc > 0.0 ? -b - sqrt(disc) : -1.0;
                    if (_BlendCam.w < 0.5 || t <= 0.0) return float4(d, 0.0);
                    p = d * t;
                }
                float3 q = p + _BlendMoved.xyz;
                // (a point of a craft moved with the craft: back where the craft had it)
                if (inside(p, _BlendAC, _BlendAX, _BlendAY, _BlendAZ)) q -= _BlendAMove.xyz + cross(_BlendASpin.xyz, p - _BlendAPivot.xyz);
                else if (inside(p, _BlendBC, _BlendBX, _BlendBY, _BlendBZ)) q -= _BlendBMove.xyz + cross(_BlendBSpin.xyz, p - _BlendBPivot.xyz);
                return float4(q, 1.0);
            }

            float4 frag(v2f IN) : SV_Target
            {
                float4 result = float4(0.0, 0.0, 0.0, 0.0);
                float2 uv = IN.vs_TEXCOORD0;
                float4 here = SampleLod(_BlendScene, uv, 0.0);
                float depth = _BlendRight.w > 0.5 ? ahead(uv) : -1.0;
                float4 q = was(uv, depth);
                // where it stood in the last frame's picture
                float f = dot(q.xyz, _BlendPrevAhead.xyz);
                if (f <= 1e-4) { result = here; return result; }
                float2 then = float2(dot(q.xyz, _BlendPrevRight.xyz) / (f * _BlendPrevRight.w), dot(q.xyz, _BlendPrevUp.xyz) / (f * _BlendPrevUp.w)) * 0.5 + 0.5;
                // the way it went while the shutter was open, as a part of the picture (its width counted in heights for the longest)
                float2 went = (uv - then) * _BlendShutter.x;
                float2 wentTall = went * float2(_BlendShutter.w, 1.0);
                float smear = length(wentTall);
                if (smear > _BlendShutter.z) { went *= _BlendShutter.z / smear; smear = _BlendShutter.z; }
                // (under about half a pixel of the picture's height, nothing to blend)
                if (smear * _BlendPrevAhead.w < 0.5) { result = here; return result; }
                float3 colour = ((float3)(0.0));
                float weight = 0.0;
                // (as many looks as the smear is long, one for every two pixels and a half of it, two at the least and at the most as many
                // as asked for: a short smear gains nothing from twelve, and every look is two reads)
                int looks = int(min(_BlendShutter.y, max(2.0, ceil(smear * _BlendPrevAhead.w / 2.5) + 1.0)));
                float jitter = chance(FragCoord(IN.pos).xy + _BlendUp.w * 7.31);
                { [loop] for (int n = 0; n < 16; n++)
                {
                    if (n >= looks) break;
                    float2 at = uv + went * ((float(n) + jitter) / float(looks) - 0.5);
                    // (what stands well in front of this pixel's own point is not blended into it: the ground streaming past a craft
                    // does not take the craft's colours along)
                    float w = 1.0;
                    if (depth > 0.0 || _BlendRight.w > 0.5)
                    {
                        float d = _BlendRight.w > 0.5 ? ahead(at) : -1.0;
                        float mine = depth > 0.0 ? depth : 1e30;
                        if (d > 0.0 && d < mine * 0.8) w = 0.0;
                    }
                    colour += SampleLod(_BlendScene, at, 0.0).rgb * w;
                    weight += w;
                } }
                result = weight > 0.5 ? float4(colour / weight, here.a) : here;
                return result;
            }
            ENDCG
        }
    }
}
