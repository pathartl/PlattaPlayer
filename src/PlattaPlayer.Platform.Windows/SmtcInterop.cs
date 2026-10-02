using System.Runtime.InteropServices;
using Windows.Media;
using WinRT;

namespace PlattaPlayer.Platform.Windows;

/// <summary>
/// Win32 bridge to <see cref="SystemMediaTransportControls"/>. A classic desktop app has no
/// CoreWindow, so SMTC must be acquired through the <c>ISystemMediaTransportControlsInterop</c>
/// COM interface, passing the top-level window handle to <c>GetForWindow</c>.
///
/// The .NET runtime cannot marshal an <c>IInspectable</c>-derived <c>[ComImport]</c> interface, so we
/// call <c>GetForWindow</c> directly through its COM vtable: the method sits at slot 6, immediately
/// after the three <c>IUnknown</c> and three <c>IInspectable</c> entries.
/// </summary>
internal static class SmtcInterop
{
    // IID of Windows.Media.ISystemMediaTransportControls (default interface of the runtime class).
    private static readonly Guid ISystemMediaTransportControlsIid = new("99FA3FF4-1742-42A6-902E-087D41F965EC");

    // IID of ISystemMediaTransportControlsInterop.
    private static readonly Guid ISystemMediaTransportControlsInteropIid = new("ddb0472d-c911-4a1f-86d9-dc3d71a95f5a");

    private const int GetForWindowVtableSlot = 6;

    public static unsafe SystemMediaTransportControls GetForWindow(IntPtr hwnd)
    {
        using var factory = ActivationFactory.Get("Windows.Media.SystemMediaTransportControls");

        var interopIid = ISystemMediaTransportControlsInteropIid;
        var hr = Marshal.QueryInterface(factory.ThisPtr, ref interopIid, out var interopPtr);
        Marshal.ThrowExceptionForHR(hr);

        try
        {
            var vtable = *(IntPtr**)interopPtr;
            var getForWindow =
                (delegate* unmanaged[Stdcall]<IntPtr, IntPtr, Guid*, IntPtr*, int>)vtable[GetForWindowVtableSlot];

            var smtcIid = ISystemMediaTransportControlsIid;
            IntPtr smtcAbi;
            hr = getForWindow(interopPtr, hwnd, &smtcIid, &smtcAbi);
            Marshal.ThrowExceptionForHR(hr);

            try
            {
                return MarshalInspectable<SystemMediaTransportControls>.FromAbi(smtcAbi);
            }
            finally
            {
                Marshal.Release(smtcAbi);
            }
        }
        finally
        {
            Marshal.Release(interopPtr);
        }
    }
}
