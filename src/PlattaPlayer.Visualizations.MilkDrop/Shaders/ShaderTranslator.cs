using System.Text;
using System.Text.RegularExpressions;

namespace PlattaPlayer.Visualizations.MilkDrop.Shaders;

/// <summary>
/// Translates a MilkDrop2 preset pixel shader (a small, constrained dialect of Direct3D9 HLSL) into a
/// GLSL ES fragment shader our OpenGL renderer can compile. This is the butterchurn approach: rather than
/// a full HLSL compiler, we lean on how close HLSL and GLSL already are — most of the work is a fixed
/// preamble that declares MilkDrop's standard uniforms/samplers and <c>#define</c>s the HLSL spellings
/// (<c>float3</c>→<c>vec3</c>, <c>lerp</c>→<c>mix</c>, <c>tex2D</c>→<c>texture2D</c>, …) onto their GLSL
/// equivalents, plus a couple of text fixups GLSL ES is strict about (integer literals must be floats,
/// the HLSL <c>f</c> suffix is illegal). The preset's <c>shader_body { … }</c> becomes a function the
/// generated <c>main()</c> invokes after seeding <c>uv</c>/<c>rad</c>/<c>ang</c>/<c>ret</c>.
///
/// Being pure string processing with no GPU dependency, this lives in the engine assembly so any
/// rendering head (desktop GL today, mobile later) can reuse it.
///
/// Known limitations (same class of gaps butterchurn has): blur/noise samplers are only meaningful if the
/// host binds real textures; presets that assign a <c>float4</c> (e.g. a raw <c>tex2D</c>) straight into
/// the <c>float3 ret</c> rely on HLSL's implicit truncation, which GLSL rejects — those need an explicit
/// <c>.xyz</c>. Matrix/array integer indexing is preserved, but exotic HLSL constructs are out of scope.
/// </summary>
public static partial class ShaderTranslator
{
    /// <summary>
    /// Produces a complete GLSL ES fragment shader (version/precision header included) from a MilkDrop
    /// HLSL <paramref name="hlsl"/> shader. <paramref name="gles"/> selects the GLES (<c>#version 100</c>)
    /// vs desktop (<c>#version 120</c>) dialect; <paramref name="isComposite"/> is accepted for the comp
    /// vs warp distinction (both share the same environment today).
    /// </summary>
    public static string Translate(string hlsl, bool isComposite, bool gles)
    {
        var (prelude, body) = SplitBody(hlsl ?? string.Empty);
        prelude = Fixup(prelude);
        body = Fixup(body);
        body = FixupRetTruncation(body);

        var sb = new StringBuilder(Preamble(gles));
        sb.Append("// ---- preset prelude ----\n").Append(prelude).Append('\n');
        sb.Append("// ---- preset body ----\n");
        sb.Append("void shader_body_func() {\n").Append(body).Append("\n}\n");
        sb.Append(Main());
        return sb.ToString();
    }

    /// <summary>
    /// Splits the raw shader into the code before <c>shader_body</c> (helper functions / <c>#define</c>s)
    /// and the statements inside its braces. If no <c>shader_body</c> marker is present the whole text is
    /// treated as the body.
    /// </summary>
    private static (string Prelude, string Body) SplitBody(string hlsl)
    {
        var marker = ShaderBodyMarker().Match(hlsl);
        if (!marker.Success) return (string.Empty, hlsl);

        var prelude = hlsl[..marker.Index];
        var rest = hlsl[(marker.Index + marker.Length)..];

        var open = rest.IndexOf('{');
        if (open < 0) return (prelude, rest);

        var close = MatchingBrace(rest, open);
        var body = close < 0 ? rest[(open + 1)..] : rest[(open + 1)..close];
        return (prelude, body);
    }

    private static int MatchingBrace(string s, int open)
    {
        var depth = 0;
        for (var i = open; i < s.Length; i++)
        {
            if (s[i] == '{') depth++;
            else if (s[i] == '}' && --depth == 0) return i;
        }
        return -1;
    }

    // GLSL ES is strict where HLSL is lax: strip the HLSL float suffix and promote bare integer literals
    // used in float expressions to floats (e.g. uv*2-1 → uv*2.0-1.0, float3(1,1,1) → vec3(1.0,1.0,1.0)).
    private static string Fixup(string code)
    {
        code = FloatSuffix().Replace(code, "$1");
        code = IntLiteral().Replace(code, m => m.Value + ".0");
        return code;
    }

    // HLSL implicitly truncates a float4 (e.g. a raw tex2D) assigned to the float3 `ret`; GLSL rejects the
    // size mismatch. Wrapping a plain `ret = EXPR;` assignment in vec3(EXPR) makes GLSL truncate to the
    // first three components — exactly HLSL's behaviour — and is a no-op identity when EXPR is already a
    // vec3 or a broadcast scalar. Compound (`ret +=`) and component (`ret.x =`) writes are left untouched.
    private static string FixupRetTruncation(string code) =>
        RetAssign().Replace(code, "ret = vec3($1);");

    private static string Preamble(bool gles) =>
        (gles
            ? "#version 100\n#extension GL_OES_standard_derivatives : enable\nprecision highp float;\nprecision highp int;\n"
            : "#version 120\n")
        + """
        // ---- HLSL → GLSL spellings ----
        #define float2 vec2
        #define float3 vec3
        #define float4 vec4
        #define float2x2 mat2
        #define float3x3 mat3
        #define float4x4 mat4
        #define int2 ivec2
        #define int3 ivec3
        #define int4 ivec4
        #define half float
        #define half2 vec2
        #define half3 vec3
        #define half4 vec4
        #define static
        #define lerp mix
        #define frac fract
        #define rsqrt inversesqrt
        #define ddx dFdx
        #define ddy dFdy
        #define saturate(x) clamp(x, 0.0, 1.0)
        #define atan2(y, x) atan(y, x)
        #define mul(a, b) ((a) * (b))
        #define tex2D texture2D
        #define tex2Dlod(s, v) texture2D(s, (v).xy)
        #define tex2Dbias(s, v) texture2D(s, (v).xy)

        // ---- MilkDrop standard uniforms ----
        uniform sampler2D sampler_main;
        uniform sampler2D sampler_fw_main;
        uniform sampler2D sampler_fc_main;
        uniform sampler2D sampler_pw_main;
        uniform sampler2D sampler_pc_main;
        uniform sampler2D sampler_blur1;
        uniform sampler2D sampler_blur2;
        uniform sampler2D sampler_blur3;
        uniform sampler2D sampler_noise_lq;
        uniform sampler2D sampler_noise_lq_lite;
        uniform sampler2D sampler_noise_mq;
        uniform sampler2D sampler_noise_hq;
        uniform sampler2D sampler_pw_noise_lq;

        uniform float time;
        uniform float fps;
        uniform float frame;
        uniform float progress;
        uniform float bass, bass_att, mid, mid_att, treb, treb_att, vol;
        uniform float decay;
        uniform vec4 texsize;   // xy = pixels, zw = 1/pixels
        uniform vec4 aspect;    // x,y = aspect, z,w = 1/aspect
        uniform vec4 rand_frame;
        uniform vec4 rand_preset;
        uniform vec3 roam_cos, roam_sin, slow_roam_cos, slow_roam_sin;
        uniform float q1, q2, q3, q4, q5, q6, q7, q8;
        uniform float q9, q10, q11, q12, q13, q14, q15, q16;
        uniform float q17, q18, q19, q20, q21, q22, q23, q24;
        uniform float q25, q26, q27, q28, q29, q30, q31, q32;

        varying vec2 vUv;
        varying vec2 vUvOrig;

        // ---- MilkDrop per-pixel scratch (globals, set up in main) ----
        vec2 uv;
        vec2 uv_orig;
        float rad;
        float ang;
        vec3 ret;
        vec3 hue_shader;

        // ---- helpers ----
        float _trunc(float x) { return x < 0.0 ? -floor(-x) : floor(x); }
        float fmod(float a, float b) { return a - b * _trunc(a / b); }
        vec2 fmod(vec2 a, vec2 b) { return a - b * vec2(_trunc(a.x / b.x), _trunc(a.y / b.y)); }
        vec3 fmod(vec3 a, vec3 b) { return a - b * vec3(_trunc(a.x / b.x), _trunc(a.y / b.y), _trunc(a.z / b.z)); }
        vec4 fmod(vec4 a, vec4 b) { return a - b * vec4(_trunc(a.x / b.x), _trunc(a.y / b.y), _trunc(a.z / b.z), _trunc(a.w / b.w)); }
        float lum(vec3 v) { return dot(v, vec3(0.32, 0.49, 0.29)); }
        vec3 GetMain(vec2 c) { return texture2D(sampler_main, c).xyz; }
        vec3 GetPixel(vec2 c) { return texture2D(sampler_main, c).xyz; }
        vec3 GetBlur1(vec2 c) { return texture2D(sampler_blur1, c).xyz; }
        vec3 GetBlur2(vec2 c) { return texture2D(sampler_blur2, c).xyz; }
        vec3 GetBlur3(vec2 c) { return texture2D(sampler_blur3, c).xyz; }

        """;

    private static string Main() => """

        void main() {
            uv = vUv;
            uv_orig = vUvOrig;
            vec2 _d = (uv_orig - vec2(0.5, 0.5)) * vec2(aspect.x, aspect.y);
            rad = length(_d) * 2.0;
            ang = atan(_d.y, _d.x);
            ret = vec3(0.0);
            hue_shader = vec3(1.0);
            shader_body_func();
            gl_FragColor = vec4(ret, 1.0);
        }
        """;

    [GeneratedRegex(@"\bshader_body\b")]
    private static partial Regex ShaderBodyMarker();

    // A bare integer not part of an identifier, a float, an exponent, a hex literal, or an array index.
    [GeneratedRegex(@"(?<![\w.\[])\d+(?![\w.\]])")]
    private static partial Regex IntLiteral();

    // HLSL float suffix: 1.0f / .5f / 2f → drop the trailing f/F.
    [GeneratedRegex(@"(?<![\w.])(\d*\.\d+|\d+\.\d*|\d+)[fF](?![\w.])")]
    private static partial Regex FloatSuffix();

    // A simple `ret = EXPR;` assignment (not `ret.x =`, not `ret +=`, not `ret ==`), capturing EXPR.
    [GeneratedRegex(@"(?<![.\w])ret\s*=(?!=)\s*([^;]*?)\s*;")]
    private static partial Regex RetAssign();
}
