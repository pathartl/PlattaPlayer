using System.Runtime.InteropServices;
using System.Text;

namespace PlattaPlayer.Wmp.Harness.Interop;

/// <summary>
/// Redirects a named import in a loaded module by rewriting its Import Address Table slot.
///
/// Preferred over patching code: the IAT is just a table of function pointers the loader filled in, so
/// swapping an entry changes nothing about the module's instructions, is trivially reversible, and
/// cannot corrupt anything if the target is not found (it simply reports so). As with
/// <see cref="AlchemySeedPatch"/> this touches only the image mapped into THIS process — never the file
/// on disk.
/// </summary>
internal static unsafe class ImportPatch
{
    private static readonly List<(nint Slot, nint Original)> Applied = [];

    /// <summary>
    /// Offset of the IMPORT data directory within IMAGE_OPTIONAL_HEADER64. The directory array starts at
    /// 0x70 and imports are entry 1, so 0x70 + 8 — entry 0 is the export table, and reading that one
    /// walks a completely unrelated structure.
    /// </summary>
    private const int ImportDirectoryOffset = 0x70 + 8;

    /// <summary>
    /// Points <paramref name="functionName"/> imported from <paramref name="fromModule"/> at
    /// <paramref name="replacement"/>. Returns false if the import is not present.
    /// </summary>
    public static bool Redirect(nint module, string fromModule, string functionName, nint replacement)
    {
        var slot = FindSlot(module, fromModule, functionName);
        if (slot == 0) return false;

        var original = *(nint*)slot;
        if (!NativeMethods.VirtualProtect(slot, (nuint)sizeof(nint),
                NativeMethods.PAGE_EXECUTE_READWRITE, out var previous))
            throw new InvalidOperationException(
                $"VirtualProtect failed on the import slot at 0x{slot:X}: {Marshal.GetLastPInvokeError()}");

        *(nint*)slot = replacement;
        NativeMethods.VirtualProtect(slot, (nuint)sizeof(nint), previous, out _);

        Applied.Add((slot, original));
        return true;
    }

    public static void RestoreAll()
    {
        foreach (var (slot, original) in Applied)
        {
            if (!NativeMethods.VirtualProtect(slot, (nuint)sizeof(nint),
                    NativeMethods.PAGE_EXECUTE_READWRITE, out var previous)) continue;
            *(nint*)slot = original;
            NativeMethods.VirtualProtect(slot, (nuint)sizeof(nint), previous, out _);
        }
        Applied.Clear();
    }

    private static nint FindSlot(nint module, string fromModule, string functionName)
    {
        var b = (byte*)module;
        var nt = b + *(int*)(b + 0x3C);
        // IMAGE_NT_HEADERS64: Signature(4) + FileHeader(20) + OptionalHeader; the data directory sits at
        // offset 0x70 into the 64-bit optional header, and entry 1 is the import table.
        var optional = nt + 4 + 20;
        var importRva = *(int*)(optional + ImportDirectoryOffset);
        if (importRva == 0) return 0;

        // IMAGE_IMPORT_DESCRIPTOR: OriginalFirstThunk(0), TimeDateStamp(4), ForwarderChain(8), Name(12), FirstThunk(16)
        // The array ends with an all-zero descriptor.
        for (var d = b + importRva; *(int*)(d + 12) != 0 && *(int*)(d + 16) != 0; d += 20)
        {
            var name = Marshal.PtrToStringAnsi((nint)(b + *(int*)(d + 12)));
            if (!string.Equals(name, fromModule, StringComparison.OrdinalIgnoreCase)) continue;

            var lookupRva = *(int*)d;                 // OriginalFirstThunk: names
            var addressRva = *(int*)(d + 16);         // FirstThunk: the live pointers
            if (lookupRva == 0) lookupRva = addressRva;

            var lookup = (ulong*)(b + lookupRva);
            var addresses = (nint*)(b + addressRva);
            for (var i = 0; lookup[i] != 0; i++)
            {
                // Top bit set means imported by ordinal, which carries no name to match.
                if ((lookup[i] & 0x8000000000000000UL) != 0) continue;
                // IMAGE_IMPORT_BY_NAME: Hint(2) then a null-terminated name.
                var importName = Marshal.PtrToStringAnsi((nint)(b + (int)lookup[i] + 2));
                if (importName == functionName) return (nint)(addresses + i);
            }
        }
        return 0;
    }

    /// <summary>Lists a module's imports from one DLL, for working out what is worth redirecting.</summary>
    public static string Describe(nint module, string fromModule)
    {
        var b = (byte*)module;
        var nt = b + *(int*)(b + 0x3C);
        var importRva = *(int*)(nt + 4 + 20 + 0x70);
        if (importRva == 0) return "(no import table)";

        var sb = new StringBuilder();
        for (var d = b + importRva; *(int*)(d + 12) != 0 && *(int*)(d + 16) != 0; d += 20)
        {
            var name = Marshal.PtrToStringAnsi((nint)(b + *(int*)(d + 12)));
            if (!string.Equals(name, fromModule, StringComparison.OrdinalIgnoreCase)) continue;

            var lookupRva = *(int*)d;
            if (lookupRva == 0) lookupRva = *(int*)(d + 16);
            var lookup = (ulong*)(b + lookupRva);
            for (var i = 0; lookup[i] != 0; i++)
            {
                if ((lookup[i] & 0x8000000000000000UL) != 0) { sb.Append("#ordinal "); continue; }
                sb.Append(Marshal.PtrToStringAnsi((nint)(b + (int)lookup[i] + 2))).Append(' ');
            }
        }
        return sb.Length == 0 ? $"(no imports from {fromModule})" : sb.ToString().TrimEnd();
    }
}
