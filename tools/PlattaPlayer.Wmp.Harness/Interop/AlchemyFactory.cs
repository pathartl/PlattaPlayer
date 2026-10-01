namespace PlattaPlayer.Wmp.Harness.Interop;

/// <summary>
/// Creates the real "Alchemy" visualization out of mpvis.DLL. Unlike the wmp.dll effects this one IS a
/// registered in-proc COM server, so plain CoCreateInstance works.
///
/// Minimum call sequence to get a frame (verified against mpvis.DLL's Render at 0x180008170): create,
/// then Render(&amp;timedLevel, hdc, &amp;rc). No SetCore, no Create, no SetCurrentPreset — Render lazily
/// allocates its surfaces and actively tears down anything a prior Create() set up.
/// </summary>
internal static class AlchemyFactory
{
    /// <summary>
    /// Creates the effect with its RNG pinned to <paramref name="seed"/>, making it reproducible and
    /// usable as a frame-exact oracle. See <see cref="AlchemySeedPatch"/> for what that involves.
    /// </summary>
    public static WmpEffect CreateSeeded(uint seed)
    {
        AlchemySeedPatch.Apply(seed);

        // Pinning the seed is necessary but not sufficient: Alchemy schedules its effects against
        // timeGetTime, so without a controlled clock two runs still diverge. Redirect that import to a
        // clock the capture loop steps one frame at a time.
        var module = NativeMethods.GetModuleHandle("mpvis.DLL");
        if (module != 0 && !VirtualClockInstalled)
        {
            VirtualClockInstalled =
                ImportPatch.Redirect(module, "WINMM.dll", "timeGetTime", VirtualClock.FunctionPointer);
        }

        return Create();
    }

    /// <summary>True once mpvis's timeGetTime import has been redirected to <see cref="VirtualClock"/>.</summary>
    public static bool VirtualClockInstalled { get; private set; }

    public static WmpEffect Create()
    {
        var clsid = WmpGuids.ClsidAlchemy;
        var iid = WmpGuids.IWMPEffects;
        var hr = NativeMethods.CoCreateInstance(
            in clsid, 0, NativeMethods.CLSCTX_INPROC_SERVER, in iid, out var p);
        if (hr < 0 || p == 0)
            throw new InvalidOperationException(
                $"CoCreateInstance(CLSID_Alchemy, IID_IWMPEffects) failed: 0x{hr:X8}. " +
                "Expected mpvis.DLL registered at C:\\Program Files\\Windows Media Player\\mpvis.DLL (x64).");
        return WmpEffect.FromRawPointer(p);
    }
}
