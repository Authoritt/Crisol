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
    [PreserveSig] int SetEndpointVisibility(IntPtr a, IntPtr b);
}

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
}
