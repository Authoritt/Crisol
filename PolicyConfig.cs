using System.Runtime.InteropServices;

namespace Crisol;

internal enum ERole
{
    Console = 0,
    Multimedia = 1,
    Communications = 2,
}

// Interfaz no documentada pero estable desde Win7 (la usan SoundSwitch, EarTrumpet, nircmd).
// Solo se llama SetDefaultEndpoint; el resto son marcadores para respetar el orden de la vtable.
[Guid("F8679F50-850A-41CF-9C72-430F290290C8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IPolicyConfig
{
    [PreserveSig] int GetMixFormat(IntPtr a, IntPtr b);
    [PreserveSig] int GetDeviceFormat(IntPtr a, IntPtr b, IntPtr c);
    [PreserveSig] int ResetDeviceFormat(IntPtr a);
    [PreserveSig] int SetDeviceFormat(IntPtr a, IntPtr b, IntPtr c);
    [PreserveSig] int GetProcessingPeriod(IntPtr a, IntPtr b, IntPtr c, IntPtr d);
    [PreserveSig] int SetProcessingPeriod(IntPtr a, IntPtr b);
    [PreserveSig] int GetShareMode(IntPtr a, IntPtr b);
    [PreserveSig] int SetShareMode(IntPtr a, IntPtr b);
    [PreserveSig] int GetPropertyValue(IntPtr a, IntPtr b, IntPtr c, IntPtr d);
    [PreserveSig] int SetPropertyValue(IntPtr a, IntPtr b, IntPtr c, IntPtr d);
    [PreserveSig] int SetDefaultEndpoint([MarshalAs(UnmanagedType.LPWStr)] string deviceId, ERole role);
    [PreserveSig] int SetEndpointVisibility([MarshalAs(UnmanagedType.LPWStr)] string deviceId, int visible);
}

// Mínimo de Core Audio para escribir el nombre visible de un endpoint (lo mismo que hace
// el panel de sonido al renombrar): IMMDevice → IPropertyStore(READWRITE) → DeviceDesc.
// El registro MMDevices no sirve: solo el servicio de audio puede escribirlo, ni siquiera admin.
[StructLayout(LayoutKind.Sequential)]
internal struct PropertyKey
{
    public Guid FormatId;
    public int PropertyId;
    public PropertyKey(Guid fmtid, int pid) { FormatId = fmtid; PropertyId = pid; }
}

[StructLayout(LayoutKind.Explicit)]
internal struct PropVariant
{
    [FieldOffset(0)] public ushort vt;
    [FieldOffset(8)] public IntPtr pointerValue;
}

[Guid("886d8eeb-8cf2-4446-8d02-cdba1dbdcf99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IPropertyStoreCom
{
    [PreserveSig] int GetCount(out int count);
    [PreserveSig] int GetAt(int i, out PropertyKey key);
    [PreserveSig] int GetValue(ref PropertyKey key, out PropVariant value);
    [PreserveSig] int SetValue(ref PropertyKey key, ref PropVariant value);
    [PreserveSig] int Commit();
}

[Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMMDeviceCom
{
    [PreserveSig] int Activate(ref Guid iid, int clsCtx, IntPtr activationParams, out IntPtr iface);
    [PreserveSig] int OpenPropertyStore(int stgmAccess, out IPropertyStoreCom propertyStore);
    [PreserveSig] int GetId(out IntPtr id);
    [PreserveSig] int GetState(out int state);
}

[Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMMDeviceEnumeratorCom
{
    [PreserveSig] int EnumAudioEndpoints(int dataFlow, int stateMask, out IntPtr devices);
    [PreserveSig] int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDeviceCom device);
    [PreserveSig] int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IMMDeviceCom device);
}

[ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
internal class MMDeviceEnumeratorComClass { }

[ComImport, Guid("870AF99C-171D-4F9E-AF0D-E63DF40C2BC9")]
internal class PolicyConfigClient
{
}

public static class PolicyConfig
{
    /// <summary>Hace que el dispositivo sea la salida por defecto de Windows en los tres roles.</summary>
    public static void SetDefaultDevice(string deviceId)
    {
        var cfg = (IPolicyConfig)new PolicyConfigClient();
        try
        {
            foreach (var role in new[] { ERole.Console, ERole.Multimedia, ERole.Communications })
                Marshal.ThrowExceptionForHR(cfg.SetDefaultEndpoint(deviceId, role));
        }
        finally
        {
            Marshal.ReleaseComObject(cfg);
        }
    }

    /// <summary>Oculta un endpoint (equivale a "Deshabilitar" en el panel de sonido; reversible desde ahí).</summary>
    public static void HideEndpoint(string deviceId)
    {
        var cfg = (IPolicyConfig)new PolicyConfigClient();
        try
        {
            Marshal.ThrowExceptionForHR(cfg.SetEndpointVisibility(deviceId, 0));
        }
        finally
        {
            Marshal.ReleaseComObject(cfg);
        }
    }

    /// <summary>Cambia el nombre visible del endpoint (lo que hace el panel de sonido al renombrar).</summary>
    public static void RenameEndpoint(string deviceId, string newName)
    {
        var enumerator = (IMMDeviceEnumeratorCom)new MMDeviceEnumeratorComClass();
        try
        {
            Marshal.ThrowExceptionForHR(enumerator.GetDevice(deviceId, out var device));
            Marshal.ThrowExceptionForHR(device.OpenPropertyStore(2 /* STGM_READWRITE */, out var store));
            var key = new PropertyKey(new Guid("a45c254e-df1c-4efd-8020-67d146a850e0"), 2); // PKEY_Device_DeviceDesc
            var value = new PropVariant { vt = 31 /* VT_LPWSTR */, pointerValue = Marshal.StringToCoTaskMemUni(newName) };
            try
            {
                Marshal.ThrowExceptionForHR(store.SetValue(ref key, ref value));
                Marshal.ThrowExceptionForHR(store.Commit());
            }
            finally
            {
                Marshal.FreeCoTaskMem(value.pointerValue);
            }
        }
        finally
        {
            Marshal.ReleaseComObject(enumerator);
        }
    }
}
