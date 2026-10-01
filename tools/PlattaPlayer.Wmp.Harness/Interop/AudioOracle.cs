using System.Runtime.InteropServices;

namespace PlattaPlayer.Wmp.Harness.Interop;

/// <summary>
/// Runs the REAL audio analysis (<c>FUN_18000ac48</c>, <c>CToleranceRenderData::Update</c>) over a
/// TimedLevel buffer, so our <c>AudioSnapshot</c> can be diffed against it frame by frame.
///
/// This is the layer worth verifying first, because everything downstream is driven by its three outputs.
/// A bass level that is slightly off changes every radius the renderers draw; a beat that fires a frame
/// early or late re-randomizes every effect at the wrong moment and desynchronises the whole
/// visualization from the music. Neither shows up as anything identifiable in a rendered frame.
///
/// No constructor is needed. <c>FUN_18000ac48</c> touches only the frame counter (+0x14), the TimedLevel
/// pointer (+0x08), the beat flags (+0x1c/+0x1d), the two gap counters (+0x20/+0x24), the bass and delta
/// (+0x28/+0x30), the rect it clears (+0x60) and the 30-entry history ring (+0xC0, index at +0x1b0) —
/// and a zeroed block is exactly what <c>FUN_18000a9fc</c> leaves all of those as. Skipping the real
/// constructor also avoids the three surface objects it builds inside the structure, which this function
/// never looks at.
/// </summary>
internal sealed unsafe class AudioOracle : IDisposable
{
    private const long AnalyzeVa = 0x18000ac48;

    /// <summary>
    /// Comfortably past the last field the analysis writes (the history ring index at +0x1b0). The real
    /// structure is larger — it embeds three surfaces from +0x1b8 — but nothing here reaches them.
    /// </summary>
    private const int RenderDataSize = 0x400;

    private readonly void* _renderData;

    public AudioOracle() => _renderData = NativeMemory.AllocZeroed(RenderDataSize);

    /// <summary>Advances one frame over the given TimedLevel block (a pinned 0x1010-byte buffer).</summary>
    public void Analyze(void* timedLevel)
    {
        var fn = (delegate* unmanaged[Cdecl]<void*, void*, void>)MpvisModule.At(AnalyzeVa);
        fn(_renderData, timedLevel);
    }

    /// <summary>Bass this frame: six low bins over 1200.</summary>
    public double Bass => *(double*)((byte*)_renderData + 0x28);

    /// <summary>Bass minus the 30-frame running average.</summary>
    public double BassDelta => *(double*)((byte*)_renderData + 0x30);

    public bool Beat => *((byte*)_renderData + 0x1d) != 0;

    public bool BassHit => *((byte*)_renderData + 0x1c) != 0;

    /// <summary>Frames since the last beat, as the analysis counts them.</summary>
    public int FramesSinceBeat => *(int*)((byte*)_renderData + 0x20);

    public int FramesSinceBassHit => *(int*)((byte*)_renderData + 0x24);

    public void Dispose() => NativeMemory.Free(_renderData);
}
