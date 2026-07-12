using System.IO;
using System.Windows;
using NAudio.CoreAudioApi;

namespace Crisol;

public partial class App : Application
{
    private Mutex? _mutex;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        if (e.Args.Contains("--selftest"))
        {
            RunSelfTest();
            return;
        }

        _mutex = new Mutex(true, @"Local\CrisolAudioMixer", out bool createdNew);
        if (!createdNew)
        {
            Shutdown();
            return;
        }

        DispatcherUnhandledException += (_, ex) =>
        {
            try
            {
                var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Crisol");
                Directory.CreateDirectory(dir);
                File.AppendAllText(Path.Combine(dir, "error.log"),
                    $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {ex.Exception}\r\n");
            }
            catch { }
            ex.Handled = true;
        };

        var window = new MainWindow();
        if (!e.Args.Contains("--tray"))
            window.Show();
    }

    /// <summary>
    /// Modo de verificación sin UI: enumera dispositivos, arranca el motor sobre la salida
    /// por defecto y abre una captura loopback (sin conectarla al mezclador, para no duplicar
    /// audio). Escribe el resultado en %TEMP%\crisol-selftest.log y termina.
    /// </summary>
    private void RunSelfTest()
    {
        string logPath = Path.Combine(Path.GetTempPath(), "crisol-selftest.log");
        var log = new List<string>();
        int exitCode = 0;
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            foreach (var d in enumerator.EnumerateAudioEndPoints(DataFlow.All, DeviceState.Active))
            {
                string flow = d.DataFlow == DataFlow.Render ? "OUT" : "IN ";
                log.Add($"{flow} | {d.FriendlyName}");
            }

            var def = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            using var engine = new AudioEngine();
            engine.AddOutput(def, 1.0f);
            using var tap = new SourceTap(def, AudioEngine.MixRate, 1.0f);
            Thread.Sleep(1500);
            log.Add($"ENGINE OK -> {def.FriendlyName} (bomba + salida + loopback)");
        }
        catch (Exception ex)
        {
            log.Add("FAIL: " + ex);
            exitCode = 1;
        }
        File.WriteAllLines(logPath, log);
        Shutdown(exitCode);
    }
}
