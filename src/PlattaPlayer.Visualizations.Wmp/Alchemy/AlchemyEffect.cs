using System;
using PlattaPlayer.Visualizations.Wmp.Audio;
using PlattaPlayer.Visualizations.Wmp.Framebuffer;

namespace PlattaPlayer.Visualizations.Wmp.Alchemy;

/// <summary>
/// Per-frame inputs shared with every effect: the surface size, the live <see cref="AudioSnapshot"/>,
/// the engine RNG, a frame counter and the drawing primitives. Effects read the focal point from the
/// audio snapshot (each component 0..1) and scale it by width/height.
/// </summary>
public sealed class EffectContext
{
    public int Width;
    public int Height;
    public AudioSnapshot Audio = null!;
    public Random Random = null!;
    public int Frame;
    public double Fps;
    public DrawPrimitives Draw = null!;

    /// <summary>
    /// The render-data fields at +0x3c / +0x40 that the original effects read. They are the FIELD
    /// DIMENSIONS, not a focal point.
    ///
    /// This was previously an audio-driven point orbiting the centre (<c>0.5 + cos(rot)*bass*0.5</c>,
    /// scaled by the surface size) — an invention of the old analysis doc. The decompiled WonderWave
    /// renderer (FUN_18000fe50) settles it: it computes <c>cx = focusX &gt;&gt; 1</c>,
    /// <c>cy = focusY &gt;&gt; 1</c> and draws its ring about that point, which only lands at the centre
    /// of the field if the two values are the width and height. Its radius is likewise
    /// <c>bass * (focusY &gt;&gt; 1) * Scale</c>, i.e. relative to half the field height, and SuperStar's
    /// <c>hypot(focusX, focusY)</c> is the field diagonal.
    ///
    /// With the invented version every effect drew at roughly half scale, centred on the quarter point,
    /// and jittered with the bass — which is why our output sat in one corner of the frame while the real
    /// effect's geometry spans it and runs off the edges.
    /// </summary>
    public int FocusX => Width;

    public int FocusY => Height;

    /// <summary>
    /// Surface size relative to the ~640px-wide surface the original mpvis effects were authored for.
    /// Effects multiply their absolute-pixel tunables (shift distances, radii, band widths, velocities)
    /// by this so motion and feature sizes keep the same on-screen proportions at any window size.
    /// </summary>
    public double Scale => Width / 640.0;
}

/// <summary>
/// Base class for an "Alchemy" effect (analysis Part C). An effect contributes a coordinate warp
/// (<see cref="Transform"/>, used to build the per-pixel displacement map), drawn geometry
/// (<see cref="Draw"/>), or both, and re-randomizes its tunables each time a scheduler slot selects it.
/// <see cref="Tick"/> advances any per-frame state before the warp map is built / geometry drawn.
/// </summary>
public abstract class AlchemyEffect
{
    /// <summary>True if this effect contributes a coordinate warp to the feedback displacement map.</summary>
    public bool IsWarp { get; protected init; }

    /// <summary>True if this effect draws geometry on top of the warped frame.</summary>
    public bool IsDraw { get; protected init; }

    /// <summary>
    /// What the warp map should sample when this kernel's <see cref="Transform"/> produces a source
    /// coordinate outside the field: <c>true</c> = the fixed pixel (0,0), <c>false</c> = the incoming
    /// coordinate (the pixel simply does not move).
    ///
    /// This is the shift kernel's byte at <c>+0x52</c>, read by both table builders
    /// (<c>FUN_18000d354</c>, <c>FUN_18000cd20</c>) as
    /// <c>if (sx &gt;= width) { sx = x; if (flag_0x52 == 0) sx = fixed_0x54; }</c> — the flag being ZERO
    /// selects the fixed fallback, and the fixed pair (<c>+0x54</c>, <c>+0x58</c>) is (0,0) for every
    /// kernel in this build. The base constructor <c>FUN_18000c774</c> sets the flag to 1, and only
    /// <c>CTShiftLinear</c> (<c>FUN_18000de44</c>) and <c>CToleranceShiftOScope</c>
    /// (<c>FUN_18000df64</c>, via a word write of 1 at <c>+0x51</c>) clear it.
    ///
    /// This is the field's DECAY. Row 0 is repainted with the background colour every frame by
    /// <c>FUN_18000d940</c>, and the background is black (<c>vis+0xde0</c>, zero), so those two kernels
    /// pull black into every destination whose source leaves the field. Without it our field has nothing
    /// draining it and saturates, which is the long-standing "ours is bright where theirs is black".
    /// </summary>
    public bool OutOfRangeToOrigin { get; protected init; }

    /// <summary>
    /// Field size, the object's own <c>+0x08</c>/<c>+0x0c</c>. The original sets these through the shared
    /// resize (<c>FUN_18000ae60</c>) BEFORE an effect is randomized, and at least one randomizer reads
    /// them — the atom balls seed their starting position with <c>rand() % width</c>. The defaults are the
    /// real field size, which Alchemy fixes at 640x480 regardless of the window.
    /// </summary>
    public int FieldWidth { get; set; } = 640;

    public int FieldHeight { get; set; } = 480;

    /// <summary>
    /// The transition envelope every pooled effect carries (fields +0x28 / +0x2c / +0x30):
    /// <c>1</c> = fading in, <c>0</c> = holding, <c>2</c> = fading out. Set up on activation by
    /// <c>FUN_18000a624</c> and walked by <c>FUN_18000a4f4</c>. Only the bass bounce reads it in this
    /// build — it blends its zoom back to identity across the fade-out — but it is modelled on the base
    /// class because the original does, and because any future effect gets it for free.
    /// </summary>
    public int Phase { get; private set; }

    /// <summary>Frames elapsed within the current <see cref="Phase"/>.</summary>
    public int PhaseCounter { get; private set; }

    /// <summary>
    /// Length of the fade-in and fade-out. Set by <c>SelectEffects</c> through a double-evaluated
    /// <c>min</c> macro, so it is NOT <c>min(lifetime/3, rand 10..90)</c> — see
    /// <see cref="EffectScheduler"/>. Always at least 10 in practice.
    /// </summary>
    public int PhaseDuration { get; private set; } = 1;

    /// <summary>Start the fade-in (<c>SelectEffects</c>: phase 1, counter 0).</summary>
    public void BeginPhase(int duration)
    {
        Phase = 1;
        PhaseCounter = 0;
        PhaseDuration = duration;
    }

    /// <summary>
    /// Walk the envelope one frame, per <c>CToleranceRenderStep::Render</c> (<c>0x18000a4f4</c>), which
    /// does this AFTER the effect has rendered. The fade-out starts once the owning slot has less time
    /// left than the envelope is long, so it always finishes exactly as the slot expires.
    /// </summary>
    public void AdvancePhase(int slotCountdown)
    {
        switch (Phase)
        {
            case 1:
                if (PhaseDuration <= PhaseCounter++) { Phase = 0; PhaseCounter = 0; }
                break;
            case 0:
                if (slotCountdown <= PhaseDuration) Phase = 2;
                break;
            default:
                PhaseCounter++;
                break;
        }
    }

    /// <summary>Display name surfaced to the host (for the "current preset" label).</summary>
    public abstract string Name { get; }

    /// <summary>Re-roll the effect's tunable parameters within their documented random ranges.</summary>
    public virtual void Randomize(Random random) { }

    /// <summary>Advance per-frame state (called once per frame before warp/draw).</summary>
    public virtual void Tick(EffectContext ctx) { }

    /// <summary>
    /// Map a destination pixel (x,y) to the source pixel it should sample from the previous frame.
    /// Coordinates are mutated in place; the caller wraps them into range afterwards.
    /// </summary>
    public virtual void Transform(EffectContext ctx, ref int x, ref int y) { }

    /// <summary>Draw this effect's audio-driven geometry into the (already warped) frame.</summary>
    public virtual void Draw(PixelBuffer buffer, EffectContext ctx) { }
}
