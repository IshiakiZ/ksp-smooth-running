#ifdef VERTEX
#version 150
// Smooth Running: frame blending (Blend.cs). One triangle over the whole picture.
in vec3 in_POSITION0;
out vec2 vs_TEXCOORD0;
void main()
{
    gl_Position = vec4(in_POSITION0.xy, 0.5, 1.0);
    vs_TEXCOORD0 = in_POSITION0.xy * 0.5 + 0.5;
}
#endif
#ifdef FRAGMENT
#version 150
// Each frame blended along the way each thing in it moved while it was being "exposed", as a film camera's shutter blends
// it: at a low frame rate the jumps between frames read as motion, not as a string of stills. What moved is reckoned for
// each pixel from where it stands (the near camera's depth picture; beyond it the planet's own round ground, or the sky,
// infinitely far) and from how the camera, and the craft it shows, moved against the planet since the last frame. The
// craft the camera follows moves with it, and stays sharp; the ground streams past it.
uniform vec4 _ZBufferParams;
uniform vec4 _BlendRight;      // xyz: the camera's right, times the tangent of half its width of view; w: 1 if its depth picture can be used
uniform vec4 _BlendUp;         // xyz: its up, times the tangent of half its height of view; w: a number that changes every frame
uniform vec4 _BlendAhead;      // xyz: the way it looks (a unit); w: 1 if depths run backwards (nearest is 1)
uniform vec4 _BlendPrevRight;  // xyz: the camera's right at the last frame, in this frame's axes as the planet turned them (a unit); w: the tangent of half its width of view then
uniform vec4 _BlendPrevUp;     // xyz: its up then (a unit); w: the tangent of half its height of view then
uniform vec4 _BlendPrevAhead;  // xyz: the way it looked then (a unit); w: the picture's height in pixels
uniform vec4 _BlendMoved;      // xyz: how far the camera moved against the planet since the last frame (m, this frame's axes); w: the far ground's distance from the planet's centre (m)
uniform vec4 _BlendCam;        // xyz: the camera, from the planet's centre (m); w: 1 where the planet has ground to meet
uniform vec4 _BlendShutter;    // x: the part of the motion since the last frame that is blended (the shutter's share of a frame); y: how many looks along it; z: the longest blend, as a part of the picture's height; w: the picture's width over its height
uniform vec4 _BlendAC;         // a craft: xyz the middle of its box, from the camera (m); w 1 if there is one
uniform vec4 _BlendAX;         // xyz: the box's first axis, over its half-length (so a point inside lies within -1 to 1 along each)
uniform vec4 _BlendAY;
uniform vec4 _BlendAZ;
uniform vec4 _BlendAPivot;     // xyz: the point the craft turned about (its root part), from the camera (m)
uniform vec4 _BlendAMove;      // xyz: how far that point moved against the planet since the last frame (m, this frame's axes)
uniform vec4 _BlendASpin;      // xyz: how far the craft turned since the last frame (an axis, times the angle in radians)
uniform vec4 _BlendBC;         // a second craft, the same
uniform vec4 _BlendBX;
uniform vec4 _BlendBY;
uniform vec4 _BlendBZ;
uniform vec4 _BlendBPivot;
uniform vec4 _BlendBMove;
uniform vec4 _BlendBSpin;
uniform sampler2D _BlendScene;           // the picture so far
uniform sampler2D _CameraDepthTexture;   // how far off each of its points is, for the near camera
in vec2 vs_TEXCOORD0;
out vec4 SV_Target0;

float chance(vec2 p) { return fract(sin(dot(p, vec2(12.9898, 78.233))) * 43758.5453); }

// How far ahead of the camera a pixel's point lies (along the way it looks), or a negative number where the depth picture
// has nothing there (the far camera's ground, the sky).
float ahead(vec2 uv)
{
    float raw = textureLod(_CameraDepthTexture, uv, 0.0).r;
    bool none = _BlendAhead.w > 0.5 ? raw <= 1e-6 : raw >= 0.999999;
    if (none) return -1.0;
    return 1.0 / (_ZBufferParams.z * raw + _ZBufferParams.w);
}

// Whether a point (from the camera) is inside a craft's box.
bool inside(vec3 p, vec4 c, vec4 x, vec4 y, vec4 z)
{
    if (c.w < 0.5) return false;
    vec3 d = p - c.xyz;
    return abs(dot(d, x.xyz)) <= 1.0 && abs(dot(d, y.xyz)) <= 1.0 && abs(dot(d, z.xyz)) <= 1.0;
}

// Where a pixel's point was at the last frame, from the camera as it stood then (this frame's axes); w 0 if infinitely far.
vec4 was(vec2 uv, float depth)
{
    vec2 s = uv * 2.0 - 1.0;
    vec3 ray = _BlendAhead.xyz + s.x * _BlendRight.xyz + s.y * _BlendUp.xyz;
    vec3 p;
    if (depth > 0.0) p = ray * depth;
    else
    {
        // beyond the near camera: the planet's round ground where the line of sight meets it, else the sky
        vec3 d = normalize(ray);
        float b = dot(_BlendCam.xyz, d), c = dot(_BlendCam.xyz, _BlendCam.xyz) - _BlendMoved.w * _BlendMoved.w;
        float disc = b * b - c;
        float t = disc > 0.0 ? -b - sqrt(disc) : -1.0;
        if (_BlendCam.w < 0.5 || t <= 0.0) return vec4(d, 0.0);
        p = d * t;
    }
    vec3 q = p + _BlendMoved.xyz;
    // (a point of a craft moved with the craft: back where the craft had it)
    if (inside(p, _BlendAC, _BlendAX, _BlendAY, _BlendAZ)) q -= _BlendAMove.xyz + cross(_BlendASpin.xyz, p - _BlendAPivot.xyz);
    else if (inside(p, _BlendBC, _BlendBX, _BlendBY, _BlendBZ)) q -= _BlendBMove.xyz + cross(_BlendBSpin.xyz, p - _BlendBPivot.xyz);
    return vec4(q, 1.0);
}

void main()
{
    vec2 uv = vs_TEXCOORD0;
    vec4 here = textureLod(_BlendScene, uv, 0.0);
    float depth = _BlendRight.w > 0.5 ? ahead(uv) : -1.0;
    vec4 q = was(uv, depth);
    // where it stood in the last frame's picture
    float f = dot(q.xyz, _BlendPrevAhead.xyz);
    if (f <= 1e-4) { SV_Target0 = here; return; }
    vec2 then = vec2(dot(q.xyz, _BlendPrevRight.xyz) / (f * _BlendPrevRight.w), dot(q.xyz, _BlendPrevUp.xyz) / (f * _BlendPrevUp.w)) * 0.5 + 0.5;
    // the way it went while the shutter was open, as a part of the picture (its width counted in heights for the longest)
    vec2 went = (uv - then) * _BlendShutter.x;
    vec2 wentTall = went * vec2(_BlendShutter.w, 1.0);
    float smear = length(wentTall);
    if (smear > _BlendShutter.z) { went *= _BlendShutter.z / smear; smear = _BlendShutter.z; }
    // (under about half a pixel of the picture's height, nothing to blend)
    if (smear * _BlendPrevAhead.w < 0.5) { SV_Target0 = here; return; }
    vec3 colour = vec3(0.0);
    float weight = 0.0;
    // (as many looks as the smear is long, one for every two pixels and a half of it, two at the least and at the most as many
    // as asked for: a short smear gains nothing from twelve, and every look is two reads)
    int looks = int(min(_BlendShutter.y, max(2.0, ceil(smear * _BlendPrevAhead.w / 2.5) + 1.0)));
    float jitter = chance(gl_FragCoord.xy + _BlendUp.w * 7.31);
    for (int n = 0; n < 16; n++)
    {
        if (n >= looks) break;
        vec2 at = uv + went * ((float(n) + jitter) / float(looks) - 0.5);
        // (what stands well in front of this pixel's own point is not blended into it: the ground streaming past a craft
        // does not take the craft's colours along)
        float w = 1.0;
        if (depth > 0.0 || _BlendRight.w > 0.5)
        {
            float d = _BlendRight.w > 0.5 ? ahead(at) : -1.0;
            float mine = depth > 0.0 ? depth : 1e30;
            if (d > 0.0 && d < mine * 0.8) w = 0.0;
        }
        colour += textureLod(_BlendScene, at, 0.0).rgb * w;
        weight += w;
    }
    SV_Target0 = weight > 0.5 ? vec4(colour / weight, here.a) : here;
}
#endif
