// The PSPSDK pspgu.h names are kept verbatim so ported code reads like the decompilation
// (`gu.Enable(GU_TEXTURE_2D)` for `sceGuEnable(GU_TEXTURE_2D)`). Import with
// `using static PlattaPlayer.Visualizations.PSP.Gu.GuConstants;`.
#pragma warning disable CA1707, IDE1006

namespace PlattaPlayer.Visualizations.PSP.Gu;

/// <summary>Subset of PSPSDK libgu constants used by visualizer_plugin.prx (src/platform/pspgu.h).</summary>
public static class GuConstants
{
    // --- primitive types
    public const int GU_POINTS = 0;
    public const int GU_LINES = 1;
    public const int GU_LINE_STRIP = 2;
    public const int GU_TRIANGLES = 3;
    public const int GU_TRIANGLE_STRIP = 4;
    public const int GU_TRIANGLE_FAN = 5;
    public const int GU_SPRITES = 6;

    // --- states for Enable/Disable
    public const int GU_ALPHA_TEST = 0;
    public const int GU_DEPTH_TEST = 1;
    public const int GU_SCISSOR_TEST = 2;
    public const int GU_STENCIL_TEST = 3;
    public const int GU_BLEND = 4;
    public const int GU_CULL_FACE = 5;
    public const int GU_DITHER = 6;
    public const int GU_FOG = 7;
    public const int GU_CLIP_PLANES = 8;
    public const int GU_TEXTURE_2D = 9;
    public const int GU_LIGHTING = 10;
    public const int GU_LIGHT0 = 11;
    public const int GU_LIGHT1 = 12;
    public const int GU_LIGHT2 = 13;
    public const int GU_LIGHT3 = 14;
    public const int GU_LINE_SMOOTH = 15;
    public const int GU_PATCH_CULL_FACE = 16;
    public const int GU_COLOR_TEST = 17;
    public const int GU_COLOR_LOGIC_OP = 18;
    public const int GU_FACE_NORMAL_REVERSE = 19;
    public const int GU_PATCH_FACE = 20;
    public const int GU_FRAGMENT_2X = 21;

    // --- vertex declaration bits
    public const int GU_TEXTURE_8BIT = 1 << 0;
    public const int GU_TEXTURE_16BIT = 2 << 0;
    public const int GU_TEXTURE_32BITF = 3 << 0;
    public const int GU_TEXTURE_BITS = 3 << 0;
    public const int GU_COLOR_5650 = 4 << 2;
    public const int GU_COLOR_5551 = 5 << 2;
    public const int GU_COLOR_4444 = 6 << 2;
    public const int GU_COLOR_8888 = 7 << 2;
    public const int GU_COLOR_BITS = 7 << 2;
    public const int GU_NORMAL_8BIT = 1 << 5;
    public const int GU_NORMAL_16BIT = 2 << 5;
    public const int GU_NORMAL_32BITF = 3 << 5;
    public const int GU_NORMAL_BITS = 3 << 5;
    public const int GU_VERTEX_8BIT = 1 << 7;
    public const int GU_VERTEX_16BIT = 2 << 7;
    public const int GU_VERTEX_32BITF = 3 << 7;
    public const int GU_VERTEX_BITS = 3 << 7;
    public const int GU_WEIGHT_BITS = 3 << 9;
    public const int GU_INDEX_8BIT = 1 << 11;
    public const int GU_INDEX_16BIT = 2 << 11;
    public const int GU_INDEX_BITS = 3 << 11;
    public const int GU_WEIGHTS_BITS = 7 << 14;
    public const int GU_VERTICES_BITS = 7 << 18;
    public const int GU_TRANSFORM_3D = 0 << 23;
    public const int GU_TRANSFORM_2D = 1 << 23;

    // --- pixel storage formats
    public const int GU_PSM_5650 = 0;
    public const int GU_PSM_5551 = 1;
    public const int GU_PSM_4444 = 2;
    public const int GU_PSM_8888 = 3;
    public const int GU_PSM_T4 = 4;
    public const int GU_PSM_T8 = 5;
    public const int GU_PSM_T16 = 6;
    public const int GU_PSM_T32 = 7;

    // --- texture functions
    public const int GU_TFX_MODULATE = 0;
    public const int GU_TFX_DECAL = 1;
    public const int GU_TFX_BLEND = 2;
    public const int GU_TFX_REPLACE = 3;
    public const int GU_TFX_ADD = 4;
    public const int GU_TCC_RGB = 0;
    public const int GU_TCC_RGBA = 1;

    // --- blending
    public const int GU_ADD = 0;
    public const int GU_SUBTRACT = 1;
    public const int GU_REVERSE_SUBTRACT = 2;
    public const int GU_MIN = 3;
    public const int GU_MAX = 4;
    public const int GU_ABS = 5;
    public const int GU_SRC_COLOR = 0;
    public const int GU_ONE_MINUS_SRC_COLOR = 1;
    public const int GU_SRC_ALPHA = 2;
    public const int GU_ONE_MINUS_SRC_ALPHA = 3;
    public const int GU_DST_ALPHA = 4;
    public const int GU_ONE_MINUS_DST_ALPHA = 5;
    public const int GU_FIX = 10;
    public const int GU_DST_COLOR = 0;
    public const int GU_ONE_MINUS_DST_COLOR = 1;

    // --- test functions
    public const int GU_NEVER = 0;
    public const int GU_ALWAYS = 1;
    public const int GU_EQUAL = 2;
    public const int GU_NOTEQUAL = 3;
    public const int GU_LESS = 4;
    public const int GU_LEQUAL = 5;
    public const int GU_GREATER = 6;
    public const int GU_GEQUAL = 7;

    // --- clear flags
    public const int GU_COLOR_BUFFER_BIT = 1;
    public const int GU_STENCIL_BUFFER_BIT = 2;
    public const int GU_DEPTH_BUFFER_BIT = 4;
    public const int GU_FAST_CLEAR_BIT = 16;

    public const int GU_CW = 0;
    public const int GU_CCW = 1;
    public const int GU_TEXTURE_COORDS = 0;
    public const int GU_TEXTURE_MATRIX = 1;
    public const int GU_ENVIRONMENT_MAP = 2;
}
