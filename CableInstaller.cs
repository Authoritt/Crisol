using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;

namespace Crisol;

/// <summary>
/// Descarga e instala el driver gratuito VB-CABLE (vb-audio.com): el dispositivo virtual
/// "CABLE Input" que hace de embudo para que todo el audio de Windows entre a Crisol
/// sin sonar por sí mismo (elimina el lag/eco del modo espejo).
/// </summary>
public static class CableInstaller
{
    private static readonly string[] Urls =
    {
        "https://download.vb-audio.com/Download_CABLE/VBCABLE_Driver_Pack45.zip",
        "https://download.vb-audio.com/Download_CABLE/VBCABLE_Driver_Pack43.zip",
    };

    public static async Task<string> DownloadAsync()
    {
        string zipPath = Path.Combine(Path.GetTempPath(), "crisol-vbcable.zip");
        string dir = Path.Combine(Path.GetTempPath(), "crisol-vbcable");

        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(3) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("Crisol/1.0");
        Exception? lastError = null;
        foreach (var url in Urls)
        {
            try
            {
                var bytes = await http.GetByteArrayAsync(url);
                await File.WriteAllBytesAsync(zipPath, bytes);
                lastError = null;
                break;
            }
            catch (Exception ex) { lastError = ex; }
        }
        if (lastError != null)
            throw new InvalidOperationException("No se pudo descargar VB-CABLE: " + lastError.Message);

        if (Directory.Exists(dir))
            Directory.Delete(dir, true);
        ZipFile.ExtractToDirectory(zipPath, dir);

        return Directory.GetFiles(dir, "VBCABLE_Setup_x64.exe", SearchOption.AllDirectories).FirstOrDefault()
            ?? throw new InvalidOperationException("El paquete descargado no contiene VBCABLE_Setup_x64.exe.");
    }

    /// <summary>Instalación silenciosa elevada (-i -h). Lanza OperationCanceledException si se rechaza el UAC.</summary>
    public static async Task<int> InstallAsync(string setupPath)
    {
        var psi = new ProcessStartInfo
        {
            FileName = setupPath,
            Arguments = "-i -h",
            UseShellExecute = true,
            Verb = "runas",
        };
        try
        {
            using var p = Process.Start(psi)
                ?? throw new InvalidOperationException("No se pudo iniciar el instalador.");
            await p.WaitForExitAsync();
            return p.ExitCode;
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            throw new OperationCanceledException("Instalación cancelada en el UAC.");
        }
    }
}
