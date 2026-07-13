using System.IO;
using System.Windows;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

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
        if (e.Args.Contains("--diag"))
        {
            RunDiag();
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

    /// <summary>
    /// Diagnóstico audible + contable en %TEMP%\crisol-diag.log:
    /// (1) volumen/mute/formato de cada salida activa, (2) un tono directo a cada salida
    /// (¿el hardware suena?), (3) loopback del default con amplitud medida (¿la captura
    /// entrega datos?), (4) la cadena completa de Crisol con la config guardada del usuario.
    /// </summary>
    private void RunDiag()
    {
        string logPath = Path.Combine(Path.GetTempPath(), "crisol-diag.log");
        var log = new List<string>();
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            var renders = enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active).ToList();
            var def = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);

            foreach (var d in renders)
            {
                string fmt;
                try { var mf = d.AudioClient.MixFormat; fmt = $"{mf.SampleRate}Hz/{mf.Channels}ch"; }
                catch (Exception ex) { fmt = "mixformat? " + ex.Message; }
                var v = d.AudioEndpointVolume;
                log.Add($"RENDER | {d.FriendlyName} | vol={(int)(v.MasterVolumeLevelScalar * 100)}% mute={v.Mute} default={d.ID == def.ID} | {fmt}");
            }

            double freq = 440;
            foreach (var d in renders)
            {
                log.Add($"TONE {freq:F0}Hz -> {d.FriendlyName}");
                try
                {
                    using var toneOut = new WasapiOut(d, AudioClientShareMode.Shared, true, 100);
                    var sig = new SignalGenerator(48000, 2) { Gain = 0.25, Frequency = freq, Type = SignalGeneratorType.Sin };
                    toneOut.Init(new SampleToWaveProvider(sig.Take(TimeSpan.FromSeconds(1.5))));
                    toneOut.Play();
                    Thread.Sleep(1900);
                    log.Add("  tono reproducido sin error");
                }
                catch (Exception ex) { log.Add("  FALLO tono: " + ex.Message); }
                freq += 220;
            }

            try
            {
                long bytes = 0; float peak = 0;
                using var cap = new WasapiLoopbackCapture(def);
                cap.DataAvailable += (_, a) =>
                {
                    bytes += a.BytesRecorded;
                    for (int i = 0; i + 3 < a.BytesRecorded; i += 4)
                    {
                        float s = Math.Abs(BitConverter.ToSingle(a.Buffer, i));
                        if (s > peak) peak = s;
                    }
                };
                using var toneOut = new WasapiOut(def, AudioClientShareMode.Shared, true, 100);
                var sig = new SignalGenerator(48000, 2) { Gain = 0.25, Frequency = 500, Type = SignalGeneratorType.Sin };
                toneOut.Init(new SampleToWaveProvider(sig.Take(TimeSpan.FromSeconds(2.5))));
                cap.StartRecording();
                toneOut.Play();
                Thread.Sleep(3000);
                cap.StopRecording();
                log.Add($"LOOPBACK {def.FriendlyName}: bytes={bytes} peak={peak:F3} (esperado: bytes>0 y peak~0.25)");
            }
            catch (Exception ex) { log.Add("FALLO loopback: " + ex.Message); }

            var cfg = AppConfig.Load();
            using (var engine = new AudioEngine())
            {
                var byId = renders
                    .Concat(enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active))
                    .ToDictionary(d => d.ID);
                foreach (var oc in cfg.Outputs.Where(x => x.Enabled))
                    if (byId.TryGetValue(oc.Id, out var d)) { engine.AddOutput(d, (float)(oc.Volume / 100)); log.Add("ENGINE OUT + " + d.FriendlyName); }
                foreach (var sc in cfg.Sources.Where(x => x.Enabled))
                    if (byId.TryGetValue(sc.Id, out var d)) { engine.AddSource(d, (float)(sc.Volume / 100)); log.Add("ENGINE SRC + " + d.FriendlyName); }

                using var toneOut = new WasapiOut(def, AudioClientShareMode.Shared, true, 100);
                var sig = new SignalGenerator(48000, 2) { Gain = 0.25, Frequency = 500, Type = SignalGeneratorType.Sin };
                toneOut.Init(new SampleToWaveProvider(sig.Take(TimeSpan.FromSeconds(4))));
                toneOut.Play();
                Thread.Sleep(4300);
                log.Add($"ENGINE taps={engine.SourceCount} legs={engine.OutputCount} pumpAlive={engine.PumpAlive} " +
                        $"tapBytes={engine.TotalTapBytes} pumped={engine.TotalPumpedBytes} mixPeak={engine.MixPeak:F3}");
            }
        }
        catch (Exception ex)
        {
            log.Add("FAIL: " + ex);
        }
        File.WriteAllLines(logPath, log);
        Shutdown(0);
    }
}
