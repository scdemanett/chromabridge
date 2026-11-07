// Licensed under the MIT license. See LICENSE.txt in the project root.

using System.Runtime.InteropServices;

namespace ChromaBridge.Providers.Asus;

/// <summary>
/// The parts of ASUS's AuraServiceLib COM type library that ChromaBridge uses, declared by hand
/// so no third-party interop package is needed. The GUIDs and the member order come from the
/// type library the LightingService registers; the interfaces are dual, and calls go through
/// the vtable, so the order of members here must match the library exactly. Members that are
/// declared only to hold their vtable slot are marked as such.
/// </summary>
internal static class AuraSdk
{
    private static readonly Guid s_classId = new("05921124-5057-483E-A037-E9497B523590");

    /// <summary>Creates the SDK object the LightingService exposes. Throws when it is not registered.</summary>
    public static IAuraSdk Create()
    {
        var type = Type.GetTypeFromCLSID(s_classId, throwOnError: true)!;

        return (IAuraSdk)Activator.CreateInstance(type)!;
    }
}

[ComImport]
[Guid("3CED2297-27BD-492C-9934-D1D153B0FAC1")]
[InterfaceType(ComInterfaceType.InterfaceIsDual)]
internal interface IAuraSdk
{
    /// <summary>Lists devices of one type, or every device for 0. The result is cached per SDK object.</summary>
    [DispId(1)]
    IAuraSyncDeviceCollection Enumerate(uint deviceType);

    /// <summary>Asks for control of the lighting. Silently does nothing when another app outranks us.</summary>
    [DispId(2)]
    void SwitchMode();
}

[ComImport]
[Guid("EE69DBAE-33FF-4E45-B378-01797A59852D")]
[InterfaceType(ComInterfaceType.InterfaceIsDual)]
internal interface IAuraSdk2
{
    [DispId(1)]
    IAuraSyncDeviceCollection Enumerate(uint deviceType);

    [DispId(2)]
    void SwitchMode();

    /// <summary>Hands the lighting back to Armoury Crate.</summary>
    [DispId(3)]
    void ReleaseControl(uint reserved);
}

[ComImport]
[Guid("87FC56AB-99CA-4FD3-B561-2EEDD719DA57")]
[InterfaceType(ComInterfaceType.InterfaceIsDual)]
internal interface IAuraSyncDeviceCollection
{
    /// <summary>Vtable slot only (_NewEnum); use Count and Item instead.</summary>
    [DispId(-4)]
    [return: MarshalAs(UnmanagedType.IUnknown)]
    object NewEnum();

    [DispId(2)]
    int Count { get; }

    [DispId(0)]
    IAuraSyncDevice this[int index] { [return: MarshalAs(UnmanagedType.Interface)] get; }
}

[ComImport]
[Guid("6A30D789-F5DA-4F26-BF09-6DFB9DEDF91E")]
[InterfaceType(ComInterfaceType.InterfaceIsDual)]
internal interface IAuraSyncDevice
{
    [DispId(7)]
    IAuraRgbLightCollection Lights { [return: MarshalAs(UnmanagedType.Interface)] get; }

    [DispId(8)]
    uint Type { get; }

    [DispId(9)]
    string Name { [return: MarshalAs(UnmanagedType.BStr)] get; }

    [DispId(13)]
    uint Width { get; }

    [DispId(14)]
    uint Height { get; }

    /// <summary>Sends the colors set on the lights to the hardware.</summary>
    [DispId(1)]
    void Apply();
}

[ComImport]
[Guid("003065BC-B562-454E-ADCB-A9B70042D486")]
[InterfaceType(ComInterfaceType.InterfaceIsDual)]
internal interface IAuraRgbLightCollection
{
    /// <summary>Vtable slot only (_NewEnum); use Count and Item instead.</summary>
    [DispId(-4)]
    [return: MarshalAs(UnmanagedType.IUnknown)]
    object NewEnum();

    [DispId(2)]
    int Count { get; }

    [DispId(0)]
    IAuraRgbLight this[int index] { [return: MarshalAs(UnmanagedType.Interface)] get; }
}

[ComImport]
[Guid("9AF6260E-4311-417D-B3EF-B85A34CF3244")]
[InterfaceType(ComInterfaceType.InterfaceIsDual)]
internal interface IAuraRgbLight
{
    [DispId(1)]
    byte Red { get; set; }

    [DispId(2)]
    byte Green { get; set; }

    [DispId(3)]
    byte Blue { get; set; }

    [DispId(4)]
    string Name { [return: MarshalAs(UnmanagedType.BStr)] get; }

    /// <summary>The color as a COLORREF: 0x00BBGGRR.</summary>
    [DispId(5)]
    uint Color { get; set; }
}
