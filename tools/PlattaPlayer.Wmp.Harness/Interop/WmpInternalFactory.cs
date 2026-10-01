using System.Runtime.InteropServices;
using System.Text;

namespace PlattaPlayer.Wmp.Harness.Interop;

/// <summary>A section of the loaded PE image.</summary>
internal readonly record struct PeSection(string Name, nint Base, int Size)
{
    public bool Contains(nint address) => address >= Base && address < Base + Size;
}

/// <summary>Where an internal class factory was found, and how far it drifted from the decompile.</summary>
internal readonly record struct InternalFactory(Guid Clsid, nint ClsidAddress, nint TableEntry, nint CreateFn);

/// <summary>
/// Locates the class factories for wmp.dll's INTERNAL visualizations — "Bars and Waves"
/// ({48501FF0-...}) and "Battery" ({8D2A317B-...}).
///
/// Neither is registered in HKCR\CLSID, and wmp.dll's exported DllGetClassObject does not serve them:
/// they are absent from the ATL object map it walks. They live instead in a private 0x20-stride table
/// in .data whose entries are { const CLSID* pClsid; HRESULT (*createFn)(IUnknown* outer, REFIID, void**); ... }.
///
/// Resolution is anchored on the CLSID BYTES, not on code patterns: a GUID is an absolute invariant
/// across builds, whereas instruction bytes and absolute VAs drift with every servicing update. The
/// scan runs over the LOADED image, so relocations and any raw/virtual skew are already applied.
///
/// Correctness is not asserted from the pointer arithmetic — it is proven by the caller's self-check
/// gate (Bars must report 4 presets titled Bars/Ocean Mist/Fire Storm/Scope; Battery must report 25).
/// </summary>
internal static unsafe class WmpInternalFactory
{
    /// <summary>Stride of the private creator table, measured on 12.0.26100.8875.</summary>
    private const int TableStride = 0x20;

    // Reference VAs from the Ghidra decompile of 12.0.26100.8875, logged so build drift is visible.
    private const long RefBarsCreateFn = 0x18041CDA0;
    private const long RefBatteryCreateFn = 0x18040BA10;

    private static nint _module;
    private static PeSection[] _sections = [];

    public static nint Module => _module;

    public static void Load()
    {
        if (_module != 0) return;

        var path = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.System), "wmp.dll");
        _module = NativeMethods.LoadLibraryEx(path, 0, NativeMethods.LOAD_WITH_ALTERED_SEARCH_PATH);
        if (_module == 0)
            throw new InvalidOperationException(
                $"LoadLibraryEx(\"{path}\") failed, error {Marshal.GetLastPInvokeError()}.");

        _sections = ReadSections(_module);
    }

    public static string DescribeModule()
    {
        var path = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.System), "wmp.dll");
        var vi = System.Diagnostics.FileVersionInfo.GetVersionInfo(path);
        var sb = new StringBuilder();
        sb.AppendLine($"  wmp.dll        {path}");
        sb.AppendLine($"  version        {vi.FileVersion}");
        sb.AppendLine($"  imagebase      0x{_module:X}");
        foreach (var s in _sections)
            sb.AppendLine($"  section {s.Name,-8} base 0x{s.Base:X}  size 0x{s.Size:X}");
        return sb.ToString();
    }

    public static InternalFactory Resolve(Guid clsid)
    {
        Load();

        var rdata = Section(".rdata");
        var data = Section(".data");
        var text = Section(".text");

        Span<byte> needle = stackalloc byte[16];
        clsid.TryWriteBytes(needle);

        var clsidHits = ScanBytes(rdata, needle);
        if (clsidHits.Count == 0)
            throw new InvalidOperationException($"CLSID {clsid:B} not found in wmp.dll .rdata.");

        var candidates = new List<InternalFactory>();
        foreach (var hit in clsidHits)
        {
            foreach (var entry in ScanQwordPointers(data, hit))
            {
                var createFn = *(nint*)(entry + 8);
                if (!text.Contains(createFn)) continue;
                if ((entry & 0xF) != 0) continue;
                if (!NeighbourLooksLikeTableEntry(entry - TableStride, rdata, text) &&
                    !NeighbourLooksLikeTableEntry(entry + TableStride, rdata, text)) continue;
                candidates.Add(new InternalFactory(clsid, hit, entry, createFn));
            }
        }

        if (candidates.Count == 0)
            throw new InvalidOperationException(
                $"Found the CLSID {clsid:B} in .rdata but no plausible creator-table entry pointing at it.");
        if (candidates.Count > 1)
            throw new InvalidOperationException(
                $"Ambiguous creator-table entries for {clsid:B}: " +
                string.Join(", ", candidates.Select(c => $"0x{c.TableEntry:X}")));

        return candidates[0];
    }

    /// <summary>
    /// Instantiate an internal effect. <paramref name="factory"/> must have come from
    /// <see cref="Resolve"/>; the caller is responsible for running the preset self-check gate.
    /// </summary>
    public static WmpEffect CreateInstance(InternalFactory factory)
    {
        var create = (delegate* unmanaged[Stdcall]<nint, Guid*, void**, int>)factory.CreateFn;
        var iid = WmpGuids.IWMPEffects;
        void* p;
        var hr = create(0, &iid, &p);
        if (hr < 0 || p is null)
            throw new InvalidOperationException(
                $"Internal creator at 0x{factory.CreateFn:X} returned 0x{hr:X8}.");
        return WmpEffect.FromRawPointer((nint)p);
    }

    public static string ReferenceDrift(Guid clsid, nint createFn)
    {
        var reference =
            clsid == WmpGuids.ClsidBarsAndWaves ? RefBarsCreateFn :
            clsid == WmpGuids.ClsidBattery ? RefBatteryCreateFn : 0;
        if (reference == 0) return "no reference VA recorded";
        var actualVa = (long)createFn - _module + 0x180000000L;
        var drift = actualVa - reference;
        return drift == 0
            ? $"matches the decompile VA 0x{reference:X}"
            : $"drifted {drift:+#;-#;0} bytes from the decompile VA 0x{reference:X} (now 0x{actualVa:X})";
    }

    /// <summary>A named section of the loaded image, for callers that need to scan it themselves.</summary>
    public static PeSection GetSection(string name)
    {
        Load();
        return Section(name);
    }

    // ---- PE walking ------------------------------------------------------------------------------

    private static PeSection Section(string name)
    {
        foreach (var s in _sections)
            if (s.Name == name) return s;
        throw new InvalidOperationException($"Section {name} not found in wmp.dll.");
    }

    private static PeSection[] ReadSections(nint moduleBase)
    {
        var b = (byte*)moduleBase;
        if (b[0] != 'M' || b[1] != 'Z') throw new InvalidOperationException("Not an MZ image.");

        var lfanew = *(int*)(b + 0x3C);
        var nt = b + lfanew;
        if (*(uint*)nt != 0x00004550) throw new InvalidOperationException("Bad PE signature.");

        var numberOfSections = *(ushort*)(nt + 6);
        var sizeOfOptionalHeader = *(ushort*)(nt + 20);
        var sectionTable = nt + 4 + 20 + sizeOfOptionalHeader;

        var list = new PeSection[numberOfSections];
        for (var i = 0; i < numberOfSections; i++)
        {
            var sh = sectionTable + i * 40;
            var name = Encoding.ASCII.GetString(sh, 8).TrimEnd('\0');
            var virtualSize = *(int*)(sh + 8);
            var virtualAddress = *(int*)(sh + 12);
            list[i] = new PeSection(name, moduleBase + virtualAddress, virtualSize);
        }
        return list;
    }

    private static List<nint> ScanBytes(PeSection section, ReadOnlySpan<byte> needle)
    {
        var hits = new List<nint>();
        var p = (byte*)section.Base;
        var end = section.Size - needle.Length;
        for (var i = 0; i <= end; i++)
        {
            if (p[i] != needle[0]) continue;
            var match = true;
            for (var j = 1; j < needle.Length; j++)
                if (p[i + j] != needle[j]) { match = false; break; }
            if (match) hits.Add(section.Base + i);
        }
        return hits;
    }

    private static List<nint> ScanQwordPointers(PeSection section, nint target)
    {
        var hits = new List<nint>();
        var p = (nint*)section.Base;
        var count = section.Size / 8;
        for (var i = 0; i < count; i++)
            if (p[i] == target) hits.Add(section.Base + i * 8);
        return hits;
    }

    private static bool NeighbourLooksLikeTableEntry(nint entry, PeSection rdata, PeSection text)
    {
        var data = Section(".data");
        if (!data.Contains(entry) || !data.Contains(entry + 15)) return false;
        var pClsid = *(nint*)entry;
        var pfn = *(nint*)(entry + 8);
        return rdata.Contains(pClsid) && text.Contains(pfn);
    }
}
