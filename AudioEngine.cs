using System.Diagnostics;
using System.IO;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace Crisol;

/// <summary>
/// Loopback WASAPI con buffer de 20 ms: NAudio usa 100 ms por defecto y sondea cada
/// media ventana, lo que metía ~50 ms de lag solo en la captura.
/// </summary>
public sealed class LowLatencyLoopbackCapture : WasapiCapture
{
    public LowLatencyLoopbackCapture(MMDevice device) : base(device, false, 20) { }

    protected override AudioClientStreamFlags GetAudioClientStreamFlags()
        => AudioClientStreamFlags.Loopback | base.GetAudioClientStreamFlags();
}

/// <summary>Mide el pico del último bloque leído; alimenta las barras de nivel de la UI.</summary>
public sealed class PeakMeter : ISampleProvider
{
    private readonly ISampleProvider _source;
    private volatile float _peak;

    public PeakMeter(ISampleProvider source) => _source = source;

    public WaveFormat WaveFormat => _source.WaveFormat;
    public float Peak => _peak;

    public int Read(float[] buffer, int offset, int count)
    {
        int read = _source.Read(buffer, offset, count);
        float max = 0;
        for (int i = 0; i < read; i++)
        {
            float a = Math.Abs(buffer[offset + i]);
            if (a > max) max = a;
        }
        _peak = max;
        return read;
    }
}

/// <summary>
/// Conversor de tasa asíncrono (ASRC) por salida. La bomba empuja la mezcla a 48 kHz de reloj
/// de PARED; cada tarjeta la consume al ritmo de SU propio reloj de hardware. Sin compensar, dos
/// salidas en la misma sala derivan hasta oírse en eco. Este proveedor lee del buffer de la
/// salida y ajusta de forma continua y suave (±0.4 %) cuántas muestras de entrada gasta por cada
/// muestra que entrega, para mantener el buffer en una latencia OBJETIVO fija. Así todas las
/// salidas quedan ancladas al mismo reloj (el de la bomba) — y por tanto en fase entre sí.
/// </summary>
public sealed class DriftCompensatingResampler : ISampleProvider
{
    private const int Channels = 2;
    private const double Kp = 0.0001;      // corrección por ms de error de latencia
    private const double MaxDev = 0.004;   // tope de ±0.4 %: inaudible, imposible que "gorjee"
    private const double HardResyncMs = 120;// error mayor = hipo real (stall/reanudar): salto de golpe

    private readonly BufferedWaveProvider _backlog; // fuente de la medida de latencia acumulada
    private readonly ISampleProvider _src;          // lectura del mismo buffer, muestra a muestra
    private readonly double _targetMs;
    private readonly float[] _one = new float[Channels];

    private double _ratio = 1.0;   // muestras de entrada consumidas por muestra de salida
    private double _frac;          // posición fraccional entre in0 e in1, en [0,1)
    private float _l0, _r0, _l1, _r1;
    private bool _primed;

    public WaveFormat WaveFormat { get; }

    public DriftCompensatingResampler(BufferedWaveProvider backlog, double targetMs)
    {
        _backlog = backlog;
        _src = backlog.ToSampleProvider();
        WaveFormat = backlog.WaveFormat;
        _targetMs = targetMs;
    }

    // ReadFully=true en el buffer ⇒ siempre devuelve el frame pedido (silencio si está vacío).
    private void PullFrame(out float l, out float r)
    {
        int n = _src.Read(_one, 0, Channels);
        if (n < Channels) { l = 0; r = 0; return; }
        l = _one[0]; r = _one[1];
    }

    // Descarta de golpe el exceso de latencia (un blip audible), solo ante un salto grande.
    private void HardResync(double excessMs)
    {
        int dropFrames = (int)(excessMs / 1000.0 * WaveFormat.SampleRate);
        var scratch = new float[Channels * 512];
        int toRead = dropFrames * Channels;
        while (toRead > 0)
        {
            int r = _src.Read(scratch, 0, Math.Min(scratch.Length, toRead));
            if (r == 0) break;
            toRead -= r;
        }
        _primed = false; _frac = 0; _ratio = 1.0;
    }

    public int Read(float[] buffer, int offset, int count)
    {
        double fillMs = _backlog.BufferedDuration.TotalMilliseconds;
        double error = fillMs - _targetMs;

        if (error > HardResyncMs)
        {
            HardResync(error);
            error = 0; // el buffer quedó en el objetivo: parte de ratio neutro, no del error viejo
        }

        // Buffer por encima del objetivo ⇒ consumir más rápido (ratio>1) para drenarlo; por
        // debajo ⇒ más lento. Ganancia pequeña ⇒ la corrección es gradual e inaudible.
        _ratio = 1.0 + Math.Clamp(Kp * error, -MaxDev, MaxDev);

        if (!_primed)
        {
            PullFrame(out _l0, out _r0);
            PullFrame(out _l1, out _r1);
            _primed = true;
        }

        int frames = count / Channels;
        for (int i = 0; i < frames; i++)
        {
            float t = (float)_frac;
            buffer[offset++] = _l0 + (_l1 - _l0) * t;
            buffer[offset++] = _r0 + (_r1 - _r0) * t;
            _frac += _ratio;
            while (_frac >= 1.0)
            {
                _frac -= 1.0;
                _l0 = _l1; _r0 = _r1;
                PullFrame(out _l1, out _r1);
            }
        }
        return frames * Channels;
    }
}

/// <summary>
/// Una fuente capturada: loopback de un dispositivo de salida (lo que suena en él)
/// o un dispositivo de captura (micrófono). Entrega audio ya convertido al formato del mezclador.
/// </summary>
public sealed class SourceTap : IDisposable
{
    // Si el buffer acumula más que esto es deriva de reloj entre dispositivos:
    // se resincroniza (salto audible breve) para que la latencia no crezca sin límite.
    private static readonly TimeSpan ResyncThreshold = TimeSpan.FromMilliseconds(150);

    private readonly IWaveIn _capture;
    private readonly BufferedWaveProvider _buffer;
    private readonly VolumeSampleProvider _volume;
    private readonly PeakMeter _meter;
    private long _bytesIn;
    private volatile bool _disposed;

    /// <summary>Se dispara (con el ID del dispositivo) si la captura muere sola (dispositivo invalidado
    /// al suspender/reanudar o al desconectarse). NAudio no reintenta: el motor lo propaga para reconstruir.</summary>
    public event Action<string>? Broken;

    public string DeviceId { get; }
    public ISampleProvider Output => _meter;
    public long BytesIn => Interlocked.Read(ref _bytesIn);
    public float Peak => _meter.Peak;

    public float Volume
    {
        get => _volume.Volume;
        set => _volume.Volume = value;
    }

    public SourceTap(MMDevice device, int mixRate, float volume)
    {
        DeviceId = device.ID;
        _capture = device.DataFlow == DataFlow.Render
            ? new LowLatencyLoopbackCapture(device)
            : new WasapiCapture(device, false, 20);

        _buffer = new BufferedWaveProvider(_capture.WaveFormat)
        {
            BufferDuration = TimeSpan.FromMilliseconds(500),
            DiscardOnBufferOverflow = true,
            ReadFully = true,
        };
        _capture.DataAvailable += (_, e) =>
        {
            Interlocked.Add(ref _bytesIn, e.BytesRecorded);
            if (_buffer.BufferedDuration > ResyncThreshold)
                _buffer.ClearBuffer();
            _buffer.AddSamples(e.Buffer, 0, e.BytesRecorded);
        };

        ISampleProvider sp = _buffer.ToSampleProvider();
        if (sp.WaveFormat.Channels == 1)
            sp = new MonoToStereoSampleProvider(sp);
        else if (sp.WaveFormat.Channels > 2)
            sp = new MultiplexingSampleProvider(new[] { sp }, 2);
        if (sp.WaveFormat.SampleRate != mixRate)
            sp = new WdlResamplingSampleProvider(sp, mixRate);

        _volume = new VolumeSampleProvider(sp) { Volume = volume };
        _meter = new PeakMeter(_volume);
        _capture.RecordingStopped += OnRecordingStopped;
        _capture.StartRecording();
    }

    private void OnRecordingStopped(object? sender, StoppedEventArgs e)
    {
        if (!_disposed) Broken?.Invoke(DeviceId);
    }

    public void Dispose()
    {
        _disposed = true;
        try { _capture.RecordingStopped -= OnRecordingStopped; } catch { }
        try { _capture.StopRecording(); } catch { /* ya detenido o dispositivo retirado */ }
        _capture.Dispose();
    }
}

/// <summary>
/// Una salida física: recibe la mezcla ya lista y la reproduce en su dispositivo
/// con su propio volumen. En WASAPI compartido convive con el audio de otras apps.
/// </summary>
public sealed class OutputLeg : IDisposable
{
    private static readonly TimeSpan Prefill = TimeSpan.FromMilliseconds(40);

    private readonly BufferedWaveProvider _buffer;
    private readonly VolumeSampleProvider _volume;
    private readonly PeakMeter _meter;
    private readonly WasapiOut _out;
    private readonly int _prefillBytes;
    private volatile bool _disposed;

    /// <summary>Se dispara (con el ID del dispositivo) si la reproducción muere sola (dispositivo invalidado
    /// al suspender/reanudar o al desconectarse). NAudio no reintenta: el motor lo propaga para reconstruir.</summary>
    public event Action<string>? Broken;

    public string DeviceId { get; }
    public float Peak => _meter.Peak;

    public float Volume
    {
        get => _volume.Volume;
        set => _volume.Volume = value;
    }

    public OutputLeg(MMDevice device, WaveFormat mixFormat, float volume)
    {
        DeviceId = device.ID;
        _buffer = new BufferedWaveProvider(mixFormat)
        {
            BufferDuration = TimeSpan.FromMilliseconds(500),
            DiscardOnBufferOverflow = true,
            ReadFully = true,
        };
        _prefillBytes = (int)(mixFormat.AverageBytesPerSecond * Prefill.TotalSeconds);
        _prefillBytes -= _prefillBytes % mixFormat.BlockAlign;
        WritePrefill();

        // El ASRC mantiene el buffer en ~Prefill de latencia atando esta salida al reloj de la
        // bomba, de modo que todas las salidas quedan en fase entre sí (antes derivaban).
        var drift = new DriftCompensatingResampler(_buffer, Prefill.TotalMilliseconds);
        _volume = new VolumeSampleProvider(drift) { Volume = volume };
        _meter = new PeakMeter(_volume);
        _out = new WasapiOut(device, AudioClientShareMode.Shared, true, 40);
        _out.Init(new SampleToWaveProvider(_meter));
        _out.PlaybackStopped += OnPlaybackStopped;
        _out.Play();
    }

    // Colchón de silencio: fija la latencia inicial en el objetivo del ASRC en vez de arrancar vacío.
    private void WritePrefill() => _buffer.AddSamples(new byte[_prefillBytes], 0, _prefillBytes);

    // La bomba empuja los MISMOS bytes a todas las salidas; el ASRC de cada una compensa la
    // deriva de su reloj. El desborde extremo lo acota DiscardOnBufferOverflow del buffer.
    public void Write(byte[] data, int count) => _buffer.AddSamples(data, 0, count);

    private void OnPlaybackStopped(object? sender, StoppedEventArgs e)
    {
        if (!_disposed) Broken?.Invoke(DeviceId);
    }

    public void Dispose()
    {
        _disposed = true;
        try { _out.PlaybackStopped -= OnPlaybackStopped; } catch { }
        _out.Dispose();
    }
}

/// <summary>
/// Motor: N fuentes → mezclador (48 kHz estéreo float) → volumen general → bomba en tiempo
/// real que reparte la MISMA mezcla a M salidas físicas (sincronizadas entre sí).
/// </summary>
public sealed class AudioEngine : IDisposable
{
    public const int MixRate = 48000;
    public static readonly WaveFormat MixFormat = WaveFormat.CreateIeeeFloatWaveFormat(MixRate, 2);

    private readonly MixingSampleProvider _mixer;
    private readonly VolumeSampleProvider _master;
    private readonly IWaveProvider _masterWave;
    private readonly Dictionary<string, SourceTap> _taps = new();
    private readonly Dictionary<string, OutputLeg> _legs = new();
    private readonly object _lock = new();
    private readonly Thread _pump;
    private volatile bool _running = true;
    private long _pumpedBytes;
    private float _mixPeak;

    public int SourceCount { get { lock (_lock) return _taps.Count; } }
    public int OutputCount { get { lock (_lock) return _legs.Count; } }
    public bool PumpAlive => _pump.IsAlive;
    public long TotalPumpedBytes => Interlocked.Read(ref _pumpedBytes);
    public long TotalTapBytes { get { lock (_lock) return _taps.Values.Sum(t => t.BytesIn); } }
    public float MixPeak => _mixPeak;

    /// <summary>Una fuente o salida murió sola (dispositivo invalidado al suspender/reanudar,
    /// cambio de driver, desconexión); entrega su ID. La UI lo escucha para reconstruir el motor
    /// con dispositivos frescos y para frenar dispositivos que se caen en bucle.</summary>
    public event Action<string>? DeviceLost;

    public float MasterVolume
    {
        get => _master.Volume;
        set => _master.Volume = value;
    }

    public AudioEngine()
    {
        _mixer = new MixingSampleProvider(MixFormat)
        {
            ReadFully = true, // rinde silencio aunque no haya fuentes: la bomba nunca se detiene
        };
        _master = new VolumeSampleProvider(_mixer) { Volume = 1.0f };
        _masterWave = new SampleToWaveProvider(_master);
        _pump = new Thread(PumpLoop) { IsBackground = true, Priority = ThreadPriority.AboveNormal, Name = "CrisolPump" };
        _pump.Start();
    }

    /// <summary>
    /// Lee la mezcla al ritmo del reloj de pared en trozos de 10 ms y escribe los MISMOS
    /// bytes en todas las salidas: eso es lo que las mantiene sincronizadas entre sí.
    /// </summary>
    private void PumpLoop()
    {
        int byteRate = MixFormat.AverageBytesPerSecond;
        int blockAlign = MixFormat.BlockAlign;
        byte[] chunk = new byte[byteRate / 100]; // 10 ms
        long maxBacklog = byteRate / 10;         // si la bomba se atrasa >100 ms (suspensión, stall), salta al presente
        var sw = Stopwatch.StartNew();
        long sent = 0;

        int errorsLogged = 0;
        while (_running)
        {
            try
            {
                long target = (long)(sw.Elapsed.TotalSeconds * byteRate);
                target -= target % blockAlign;
                if (target - sent > maxBacklog)
                    sent = target - maxBacklog;

                while (sent < target && _running)
                {
                    int n = (int)Math.Min(chunk.Length, target - sent);
                    n -= n % blockAlign;
                    if (n == 0) break;
                    int read = _masterWave.Read(chunk, 0, n);
                    if (read == 0) break;
                    for (int i = 0; i + 3 < read; i += 4)
                    {
                        float s = Math.Abs(BitConverter.ToSingle(chunk, i));
                        if (s > _mixPeak) _mixPeak = s;
                    }
                    lock (_lock)
                    {
                        foreach (var leg in _legs.Values)
                            leg.Write(chunk, read);
                    }
                    sent += read;
                    Interlocked.Add(ref _pumpedBytes, read);
                }
            }
            catch (Exception ex)
            {
                // La bomba no puede morir en silencio: sin ella todas las salidas enmudecen.
                if (errorsLogged++ < 5) LogError(ex);
                Thread.Sleep(200);
            }
            Thread.Sleep(5);
        }
    }

    private static void LogError(Exception ex)
    {
        try
        {
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Crisol");
            Directory.CreateDirectory(dir);
            File.AppendAllText(Path.Combine(dir, "error.log"),
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] PUMP: {ex}\r\n");
        }
        catch { }
    }

    public void AddSource(MMDevice device, float volume)
    {
        lock (_lock)
        {
            if (_taps.ContainsKey(device.ID)) return;
            var tap = new SourceTap(device, MixRate, volume);
            tap.Broken += id => DeviceLost?.Invoke(id);
            _taps[device.ID] = tap;
            _mixer.AddMixerInput(tap.Output);
        }
    }

    public void RemoveSource(string deviceId)
    {
        lock (_lock)
        {
            if (_taps.Remove(deviceId, out var tap))
            {
                _mixer.RemoveMixerInput(tap.Output);
                tap.Dispose();
            }
        }
    }

    public void SetSourceVolume(string deviceId, float volume)
    {
        lock (_lock)
        {
            if (_taps.TryGetValue(deviceId, out var tap))
                tap.Volume = volume;
        }
    }

    public void AddOutput(MMDevice device, float volume)
    {
        lock (_lock)
        {
            if (_legs.ContainsKey(device.ID)) return;
            var leg = new OutputLeg(device, MixFormat, volume);
            leg.Broken += id => DeviceLost?.Invoke(id);
            _legs[device.ID] = leg;
        }
    }

    public void RemoveOutput(string deviceId)
    {
        lock (_lock)
        {
            if (_legs.Remove(deviceId, out var leg))
                leg.Dispose();
        }
    }

    public void SetOutputVolume(string deviceId, float volume)
    {
        lock (_lock)
        {
            if (_legs.TryGetValue(deviceId, out var leg))
                leg.Volume = volume;
        }
    }

    public float GetSourcePeak(string deviceId)
    {
        lock (_lock)
            return _taps.TryGetValue(deviceId, out var tap) ? tap.Peak : 0f;
    }

    public float GetOutputPeak(string deviceId)
    {
        lock (_lock)
            return _legs.TryGetValue(deviceId, out var leg) ? leg.Peak : 0f;
    }

    /// <summary>Inyecta un tono en el mezclador; al terminar, el mezclador lo retira solo.</summary>
    public void PlayTestTone(double seconds, double frequency = 500)
    {
        var sig = new SignalGenerator(MixRate, 2) { Gain = 0.25, Frequency = frequency, Type = SignalGeneratorType.Sin };
        _mixer.AddMixerInput(sig.Take(TimeSpan.FromSeconds(seconds)));
    }

    public void ResetPeak() => _mixPeak = 0;

    public void RemoveAll()
    {
        lock (_lock)
        {
            foreach (var tap in _taps.Values) { _mixer.RemoveMixerInput(tap.Output); tap.Dispose(); }
            _taps.Clear();
            foreach (var leg in _legs.Values) leg.Dispose();
            _legs.Clear();
        }
    }

    public void Dispose()
    {
        _running = false;
        try { _pump.Join(500); } catch { }
        RemoveAll();
    }
}
