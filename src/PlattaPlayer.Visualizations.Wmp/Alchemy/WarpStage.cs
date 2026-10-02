using System;
using System.Collections.Generic;
using PlattaPlayer.Visualizations.Wmp.Alchemy.Effects;

namespace PlattaPlayer.Visualizations.Wmp.Alchemy;

/// <summary>
/// <c>CToleranceShift</c>: the warp stage, the single category-3 effect that slot 0 holds. It owns TEN
/// persistent kernel instances and keeps TWO warp tables: the CURRENT one the feedback pass reads, and a
/// NEXT one it builds a few rows per frame in the background.
///
/// This class models that kernel LIFECYCLE, which the port previously replaced with "pick a new kernel
/// and show it at once":
/// <list type="bullet">
/// <item><b>The visible kernel was chosen one re-roll EARLIER.</b> <c>ForceRandomize</c>
/// (<c>0x18000d4f0</c>) promotes the pending kernel pair to current and only then picks a new pending
/// pair, whose table starts building.</item>
/// <item><b>A re-roll is IGNORED while the next table is still building or a morph is running.</b>
/// <c>Randomize</c> (<c>0x18000db10</c>) only forwards to <c>ForceRandomize</c> when neither is in
/// progress. The table builds 3 rows per frame (<c>BuildNextTable</c>, <c>0x18000cd20</c>), so at 480
/// rows a slot lifetime under ~161 frames re-rolls to no effect at all.</item>
/// <item><b>Every promotion draws morph parameters</b>, and morph mode 2 draws one <c>rand()</c> per
/// frame for as long as it runs (<c>AdjustTransitionIndex</c>, <c>0x18000ca2c</c>).</item>
/// </list>
///
/// The morph's LOOK is the engine's job: <see cref="Promotions"/> tells it when to freeze the old map, and
/// <see cref="MorphIndex"/> which of the 22 blends to show (<see cref="WarpMap.Morph"/>). The GPU path does
/// not blend yet and switches in one frame.
/// </summary>
public sealed class WarpStage : AlchemyEffect
{
    /// <summary>The number of interpolation tables a morph walks through.</summary>
    private const int MorphSteps = 22;

    /// <summary>Rows <c>BuildNextTable</c> fills per frame.</summary>
    private const int RowsPerFrame = 3;

    private readonly AlchemyEffect[] _kernels;
    private readonly bool[] _taken;
    private readonly Func<int, AlchemyEffect, bool> _isType;

    // Kernel indices (-1 = none): +0x70 / +0x78 current, +0x80 / +0x88 next.
    private int _currentA = -1;
    private int _currentB = -1;
    private int _nextA = -1;
    private int _nextB = -1;

    // Both tables start with their +0x10 flag SET (CToleranceShiftData's constructor, 0x18000c750). So
    // the first selection's Randomize is gated off, and the kernels are first chosen inside the first
    // NormalRender, AFTER the selecting step has drawn its fade envelope.
    private bool _currentIncomplete = true; // current table's +0x10
    private bool _nextBuilding = true;      // next table's +0x10
    private int _nextRow;            // next table's +0x14
    private bool _transitioning;     // +0x521
    private int _morphMode;          // +0x50
    private int _morphSpeed;         // +0x54
    private int _morphIndex;         // +0x524
    private int _morphCounter;       // +0x528

    /// <param name="kernels">The stage's kernel instances, in the order <c>AddShiftEffect</c> adds them.</param>
    /// <param name="isType">Whether a kernel instance is of a given registry type (for pinning).</param>
    public WarpStage(IReadOnlyList<AlchemyEffect> kernels, Func<int, AlchemyEffect, bool> isType)
    {
        _kernels = [.. kernels];
        _taken = new bool[_kernels.Length];
        _isType = isType;
    }

    public override string Name => "Shift";

    /// <summary>The primary kernel the feedback pass currently reads, or null before the first frame.</summary>
    public AlchemyEffect? CurrentA => _currentA >= 0 ? _kernels[_currentA] : null;

    /// <summary>The optional second kernel averaged into the current table (1 in 5 selections).</summary>
    public AlchemyEffect? CurrentB => _currentB >= 0 ? _kernels[_currentB] : null;

    /// <summary>True while a morph is running: the engine shows <see cref="MorphIndex"/>'s blend.</summary>
    public bool Transitioning => _transitioning;

    /// <summary>Which of the 22 interpolation tables the morph is showing (0 = nearest the old map).</summary>
    public int MorphIndex => _morphIndex;

    /// <summary>
    /// Counts promotions of the pending kernels to current. The engine watches it to freeze the old map at
    /// the moment of promotion: the released kernels may be re-randomized straight away, so the old map
    /// cannot be rebuilt from them afterwards.
    /// </summary>
    public int Promotions { get; private set; }

    /// <summary>Diagnostic: restrict kernel selection to one registry TYPE (see <see cref="EffectRegistry"/>).</summary>
    public int? PinnedType { get; set; }

    /// <summary>
    /// <c>CToleranceShift::Randomize</c>: re-roll ONLY when no morph is running and the next table is not
    /// still being built. Otherwise the call does nothing and consumes nothing.
    /// </summary>
    public override void Randomize(Random random)
    {
        if (!_transitioning && !_nextBuilding) ForceRandomize(random);
    }

    /// <summary>
    /// <c>CToleranceShift::ForceRandomize(0)</c>. Also what the host's "next preset" maps to: the original's
    /// 'm' key reaches this directly, bypassing the gate in <see cref="Randomize"/>.
    /// </summary>
    public void ForceRandomize(Random random)
    {
        _transitioning = false;
        if (_nextA >= 0)
        {
            _transitioning = true;
            _morphIndex = 0;
            _morphCounter = 0;
            // A next table caught half-built is completed on the spot, and then there is no morph.
            if (_nextBuilding) _transitioning = false;

            _morphMode = MpvisMath.MinOfK(random, 0, 2, 3);
            _morphSpeed = random.Next(3) == 0 ? random.Next(15) + 1 : random.Next(5) + 10;
            // Any OScope involved (byte +0x51) forces the quickest morph.
            if (_transitioning && (IsOScope(_currentA) || IsOScope(_currentB) || IsOScope(_nextA) || IsOScope(_nextB)))
                _morphSpeed = 1;

            // The tables swap, the current kernels are released, and the pending pair becomes current.
            Release(_currentA);
            Release(_currentB);
            _currentA = _nextA;
            _currentB = _nextB;
            Promotions++;
            _nextA = -1;
            _nextB = -1;
        }

        _nextBuilding = true;
        _nextRow = 0;
        _nextA = SelectKernel(random);
        // rand() % 5 == 0 adds a second kernel. The draw is taken even when no primary came back. The pair
        // is then DROPPED unless both kernels have byte +0x50 set, and the OScope constructor
        // (0x18000df64) clears it. So an OScope never takes part in a pair, though the second kernel has
        // already been chosen and randomized by then, and is released again.
        if (random.Next(5) == 0 && _nextA >= 0)
        {
            _nextB = SelectKernel(random);
            if (_nextB >= 0 && (IsOScope(_nextA) || IsOScope(_nextB)))
            {
                Release(_nextB);
                _nextB = -1;
            }
        }

        if (_currentA < 0)
        {
            _currentA = SelectKernel(random);
            _currentIncomplete = true;
        }
    }

    /// <summary>
    /// Clear everything and choose afresh (diagnostic pinning only; the original has no equivalent).
    /// </summary>
    public void Restart(Random random)
    {
        Array.Clear(_taken);
        _currentA = _currentB = _nextA = _nextB = -1;
        _currentIncomplete = _nextBuilding = _transitioning = false;
        ForceRandomize(random);
    }

    /// <summary>
    /// The table bookkeeping of <c>CToleranceShift::NormalRender</c> → <c>UpdateTables</c>
    /// (<c>0x18000dc80</c>). The warp map and the feedback pass themselves are driven by the engine.
    /// </summary>
    public override void Tick(EffectContext ctx)
    {
        if (_currentA < 0) ForceRandomize(ctx.Random);

        if (_currentIncomplete)
        {
            // CompleteTable builds the whole current table in one go.
            _currentIncomplete = false;
            return;
        }

        if (_transitioning && AdjustTransitionIndex(ctx.Random)) return;
        if (_nextA >= 0) BuildNextTable(ctx.Height);
    }

    /// <summary><c>BuildNextTable</c>: three rows per call, and the building flag clears on the call that finds the row count reached.</summary>
    private void BuildNextTable(int height)
    {
        if (!_nextBuilding) return;
        for (var i = 0; i < RowsPerFrame; i++)
        {
            if (_nextRow == height)
            {
                _nextBuilding = false;
                break;
            }
            _nextRow++;
        }
    }

    /// <summary>
    /// <c>AdjustTransitionIndex</c>: steps the morph. Mode 0 eases along a sine over <c>speed·22</c> frames,
    /// mode 1 steps once every <c>speed</c> frames, and mode 2 steps on a random 1-in-<c>speed</c> chance
    /// (one draw per frame). Returns false once the morph has finished.
    /// </summary>
    private bool AdjustTransitionIndex(Random random)
    {
        var step = false;
        switch (_morphMode)
        {
            case 0:
            {
                var counter = _morphCounter++;
                if (_morphSpeed * MorphSteps < counter)
                {
                    _morphIndex = MorphSteps;
                }
                else
                {
                    var s = Math.Sin((double)_morphCounter / (_morphSpeed * MorphSteps) * (MpvisMath.Pi / 2));
                    var index = (int)(s * MorphSteps);
                    _morphIndex = index >= 0 ? index : 0;
                }
                break;
            }
            case 1:
            {
                var counter = _morphCounter++;
                step = _morphSpeed <= 0 || counter % _morphSpeed == 0;
                break;
            }
            case 2:
            {
                var r = random.Next();
                step = _morphSpeed <= 0 || r % _morphSpeed == 0;
                break;
            }
        }

        if (step) _morphIndex++;
        if (_morphIndex < MorphSteps) return true;

        _transitioning = false;
        return false;
    }

    /// <summary>
    /// <c>SelectRandomShiftEffect</c> (<c>0x18000db4c</c>): draw uniformly over the ten instances,
    /// retrying while the pick is already in use (100 tries), then randomize the pick.
    /// </summary>
    private int SelectKernel(Random random)
    {
        if (_kernels.Length == 0) return -1;

        int index;
        if (PinnedType is { } type)
        {
            // The first free instance of the pinned type, else any instance of it.
            index = -1;
            var fallback = -1;
            for (var i = 0; i < _kernels.Length; i++)
            {
                if (!_isType(type, _kernels[i])) continue;
                if (fallback < 0) fallback = i;
                if (!_taken[i]) { index = i; break; }
            }
            if (index < 0) index = fallback;
            if (index < 0) return -1;
        }
        else
        {
            var tries = 100;
            while (true)
            {
                index = MpvisMath.RandLong(random, 0, _kernels.Length - 1);
                if (tries < 1)
                {
                    if (_taken[index]) return -1;
                    break;
                }
                tries--;
                if (!_taken[index]) break;
            }
        }

        _taken[index] = true;
        _kernels[index].Randomize(random);
        return index;
    }

    private void Release(int index)
    {
        if (index >= 0) _taken[index] = false;
    }

    private bool IsOScope(int index) => index >= 0 && _kernels[index] is OScopeShift;

    /// <summary>Propagate the field size to every kernel (the stage's own resize does the same).</summary>
    public void ResizeKernels(int width, int height)
    {
        foreach (var k in _kernels)
        {
            k.FieldWidth = width;
            k.FieldHeight = height;
        }
    }
}
