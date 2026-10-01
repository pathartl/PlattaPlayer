using System.Runtime.InteropServices;

namespace PlattaPlayer.Wmp.Harness.Interop;

/// <summary>
/// The <c>IWMPEffects</c> / <c>IWMPEffects2</c> vtable, declared explicitly rather than via
/// <c>[ComImport]</c> so that the slot order stays auditable against the reverse-engineering notes.
/// Slot order was read out of mpvis.DLL's vtable at 0x180020158 and cross-checked against wmp.dll's
/// Bars vtable at 0x1807a9330: 0-2 IUnknown, 3-13 IWMPEffects, 14-19 IWMPEffects2.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal unsafe struct IWmpEffectsVtbl
{
    // 0..2 — IUnknown
    public delegate* unmanaged[Stdcall]<void*, Guid*, void**, int> QueryInterface;
    public delegate* unmanaged[Stdcall]<void*, uint> AddRef;
    public delegate* unmanaged[Stdcall]<void*, uint> Release;

    // 3..13 — IWMPEffects
    public delegate* unmanaged[Stdcall]<void*, void*, nint, RECT*, int> Render;
    public delegate* unmanaged[Stdcall]<void*, int, int, nint, int> MediaInfo;
    public delegate* unmanaged[Stdcall]<void*, uint*, int> GetCapabilities;
    public delegate* unmanaged[Stdcall]<void*, nint*, int> GetTitle;
    public delegate* unmanaged[Stdcall]<void*, int, nint*, int> GetPresetTitle;
    public delegate* unmanaged[Stdcall]<void*, int*, int> GetPresetCount;
    public delegate* unmanaged[Stdcall]<void*, int, int> SetCurrentPreset;
    public delegate* unmanaged[Stdcall]<void*, int*, int> GetCurrentPreset;
    public delegate* unmanaged[Stdcall]<void*, nint, int> DisplayPropertyPage;
    public delegate* unmanaged[Stdcall]<void*, int, int> GoFullscreen;
    public delegate* unmanaged[Stdcall]<void*, void*, int> RenderFullScreen;

    // 14..19 — IWMPEffects2 (only valid on a pointer obtained by QI for IID_IWMPEffects2)
    public delegate* unmanaged[Stdcall]<void*, void*, int> SetCore;
    public delegate* unmanaged[Stdcall]<void*, nint, int> Create;
    public delegate* unmanaged[Stdcall]<void*, int> Destroy;
    public delegate* unmanaged[Stdcall]<void*, void*, int> NotifyNewMedia;
    public delegate* unmanaged[Stdcall]<void*, nint, uint, nint, nint, nint*, int> OnWindowMessage;
    public delegate* unmanaged[Stdcall]<void*, void*, int, int> RenderWindowed;
}

internal static class WmpGuids
{
    public static readonly Guid IUnknown = new("00000000-0000-0000-C000-000000000046");
    public static readonly Guid IWMPEffects = new("D3984C13-C3CB-48E2-8BE5-5168340B4F35");
    public static readonly Guid IWMPEffects2 = new("695386EC-AA3C-4618-A5E1-DD9A8B987632");
    public static readonly Guid IDispatch = new("00020400-0000-0000-C000-000000000046");

    /// <summary>mpvis.DLL, "AlchemyVis Class" — the only coclass it registers.</summary>
    public static readonly Guid ClsidAlchemy = new("0AA02E8D-F851-4CB0-9F64-BBA9BE7A983D");

    /// <summary>wmp.dll internal "Bars and Waves". Registered under MediaPlayer\Objects\Effects but NOT in HKCR\CLSID.</summary>
    public static readonly Guid ClsidBarsAndWaves = new("48501FF0-F6A9-11D2-9435-00A0C92A2F2D");

    /// <summary>wmp.dll internal "Battery". Same story as Bars.</summary>
    public static readonly Guid ClsidBattery = new("8D2A317B-D98B-4EFA-89D6-E49F0ACF98A1");
}

/// <summary>
/// A live <c>IWMPEffects</c> instance. All calls go through the explicit vtable above.
/// </summary>
internal sealed unsafe class WmpEffect : IDisposable
{
    private void* _p;

    private WmpEffect(void* p) => _p = p;

    public static WmpEffect FromRawPointer(nint p) => new((void*)p);

    private IWmpEffectsVtbl* Vtbl => *(IWmpEffectsVtbl**)_p;

    /// <summary>The raw interface pointer, for diagnostics that need to walk the object's own vtables.</summary>
    public nint RawPointer => (nint)_p;

    private static void Check(int hr, string what)
    {
        // Windows Media HRESULTs in facility 0x0D (e.g. 0x000D1144) are successes, as is S_FALSE.
        if (hr < 0) throw new InvalidOperationException($"{what} failed: 0x{hr:X8}");
    }

    public int Render(void* timedLevel, nint hdc, ref RECT rc)
    {
        fixed (RECT* prc = &rc)
            return Vtbl->Render(_p, timedLevel, hdc, prc);
    }

    public string GetTitle()
    {
        nint bstr = 0;
        Check(Vtbl->GetTitle(_p, &bstr), nameof(GetTitle));
        return TakeBstr(ref bstr);
    }

    public string GetPresetTitle(int preset)
    {
        nint bstr = 0;
        Check(Vtbl->GetPresetTitle(_p, preset, &bstr), nameof(GetPresetTitle));
        return TakeBstr(ref bstr);
    }

    public int GetPresetCount()
    {
        int n;
        Check(Vtbl->GetPresetCount(_p, &n), nameof(GetPresetCount));
        return n;
    }

    public int SetCurrentPreset(int preset) => Vtbl->SetCurrentPreset(_p, preset);

    public int GetCurrentPreset()
    {
        int n;
        Check(Vtbl->GetCurrentPreset(_p, &n), nameof(GetCurrentPreset));
        return n;
    }

    public uint GetCapabilities()
    {
        uint caps;
        Check(Vtbl->GetCapabilities(_p, &caps), nameof(GetCapabilities));
        return caps;
    }

    /// <summary>
    /// Fallback initialisation for the wmp.dll internal effects, which — unlike Alchemy — may expect
    /// the host to have called into IWMPEffects2 first. Failures are swallowed: Alchemy explicitly
    /// discards the core pointer and undoes anything Create() set up, so this must stay optional.
    /// </summary>
    public bool TryInitializeAsEffects2()
    {
        void* p2;
        var iid = WmpGuids.IWMPEffects2;
        if (Vtbl->QueryInterface(_p, &iid, &p2) < 0) return false;
        var vt2 = *(IWmpEffectsVtbl**)p2;
        vt2->SetCore(p2, null);
        vt2->Create(p2, 0);
        vt2->Release(p2);
        return true;
    }

    private static string TakeBstr(ref nint bstr)
    {
        if (bstr == 0) return "";
        var s = Marshal.PtrToStringBSTR(bstr);
        NativeMethods.SysFreeString(bstr);
        bstr = 0;
        return s;
    }

    public void Dispose()
    {
        if (_p is null) return;
        Vtbl->Release(_p);
        _p = null;
    }
}
