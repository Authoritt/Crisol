using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Win32;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using WinForms = System.Windows.Forms;
using Drawing = System.Drawing;

namespace Crisol;

public class DeviceRow : INotifyPropertyChanged
{
    private bool _enabled;
    private double _volume = 100;
    private bool _isUnlocked = true;
    private string? _lockReason;

    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public bool IsMic { get; init; }
    public bool IsOutputRole { get; init; }

    public string Label => (IsMic ? "🎤 " : "🔊 ") + Name;

    public bool Enabled
    {
        get => _enabled;
        set { _enabled = value; Raise(nameof(Enabled)); }
    }

    public double Volume
    {
        get => _volume;
        set { _volume = value; Raise(nameof(Volume)); Raise(nameof(VolumeText)); }
    }

    public string VolumeText => ((int)Math.Round(_volume)).ToString();

    public bool IsUnlocked => _isUnlocked;
    public string? LockReason => _lockReason;

    public void SetLock(bool locked, string? reason)
    {
        _isUnlocked = !locked;
        _lockReason = locked ? reason : null;
        Raise(nameof(IsUnlocked));
        Raise(nameof(LockReason));
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void Raise(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public partial class MainWindow : Window
{
    private readonly AudioEngine _engine = new();
    private readonly MMDeviceEnumerator _enumerator = new();
    private readonly AppConfig _config = AppConfig.Load();
    private readonly ObservableCollection<DeviceRow> _sources = new();
    private readonly ObservableCollection<DeviceRow> _outputs = new();
    private readonly Dictionary<string, MMDevice> _devicesById = new();
    private readonly DispatcherTimer _saveTimer;
    private WinForms.NotifyIcon _tray = null!;
    private bool _exiting;
    private bool _loading;
    private string? _defaultRenderId;

    private enum CableAction { Install, Activate, None }
    private CableAction _cableAction = CableAction.Install;

    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValueName = "Crisol";

    public MainWindow()
    {
        InitializeComponent();
        SourcesList.ItemsSource = _sources;
        OutputsList.ItemsSource = _outputs;

        _saveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(600) };
        _saveTimer.Tick += (_, _) => { _saveTimer.Stop(); SyncConfigFromUi(); _config.Save(); };

        SetupTray();
        RefreshDevices();
        StartEngineFromUi();
        AutostartCheck.IsChecked = IsAutostartEnabled();
    }

    // ---------- dispositivos ----------

    private void RefreshDevices()
    {
        _loading = true;
        try
        {
            _devicesById.Clear();
            var renders = _enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active).ToList();
            var captures = _enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active).ToList();
            foreach (var d in renders.Concat(captures))
                _devicesById[d.ID] = d;

            if (_config.IsFirstRun)
            {
                // Primer arranque: dejar la salida por defecto ya marcada.
                string? defId = DefaultRenderId(renders);
                if (defId != null)
                    AppConfig.GetOrAdd(_config.Outputs, defId).Enabled = true;
            }

            _sources.Clear();
            foreach (var d in renders.Concat(captures))
            {
                var saved = AppConfig.GetOrAdd(_config.Sources, d.ID);
                _sources.Add(new DeviceRow
                {
                    Id = d.ID,
                    Name = SafeName(d),
                    IsMic = d.DataFlow == DataFlow.Capture,
                    Enabled = saved.Enabled,
                    Volume = saved.Volume,
                });
            }

            _defaultRenderId = DefaultRenderId(renders);

            _outputs.Clear();
            // CABLE Input es un embudo virtual mudo: como salida física no tiene sentido
            // y marcarlo bloquearía su fila de fuente, que es su único uso.
            foreach (var d in renders.Where(d => !IsCable(SafeName(d))))
            {
                var saved = AppConfig.GetOrAdd(_config.Outputs, d.ID);
                _outputs.Add(new DeviceRow
                {
                    Id = d.ID,
                    Name = SafeName(d),
                    IsOutputRole = true,
                    Enabled = saved.Enabled,
                    Volume = saved.Volume,
                });
            }

            MasterSlider.Value = Math.Clamp(_config.MasterVolume, 0, 100);
            UpdateLocks();
            UpdateCableButton();
        }
        finally
        {
            _loading = false;
        }
    }

    private static bool IsCable(string name) =>
        name.Contains("CABLE Input", StringComparison.OrdinalIgnoreCase);

    private DeviceRow? FindCableSource() =>
        _sources.FirstOrDefault(s => !s.IsMic && IsCable(s.Name));

    private void UpdateCableButton()
    {
        var cable = FindCableSource();
        if (cable == null)
        {
            _cableAction = CableAction.Install;
            CableButton.Content = "Instalar cable virtual (elimina el lag)";
            CableButton.IsEnabled = true;
        }
        else if (_defaultRenderId != cable.Id)
        {
            _cableAction = CableAction.Activate;
            CableButton.Content = "Activar cable (salida por defecto de Windows)";
            CableButton.IsEnabled = true;
        }
        else
        {
            _cableAction = CableAction.None;
            CableButton.Content = "Cable virtual activo ✓";
            CableButton.IsEnabled = false;
        }
    }

    private string? DefaultRenderId(List<MMDevice> renders)
    {
        try { return _enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia).ID; }
        catch { return renders.FirstOrDefault()?.ID; }
    }

    private static string SafeName(MMDevice d)
    {
        try { return d.FriendlyName; }
        catch { return "(dispositivo)"; }
    }

    /// <summary>
    /// Un dispositivo no puede ser fuente y salida a la vez: Crisol leería lo que él
    /// mismo escribe y se formaría un lazo de eco. El lado ya activo bloquea al otro.
    /// </summary>
    private void UpdateLocks()
    {
        bool wasLoading = _loading;
        _loading = true;
        try
        {
            var enabledOut = _outputs.Where(o => o.Enabled).Select(o => o.Id).ToHashSet();
            var enabledSrc = _sources.Where(s => s.Enabled).Select(s => s.Id).ToHashSet();

            foreach (var s in _sources.Where(s => !s.IsMic))
            {
                bool locked = enabledOut.Contains(s.Id);
                if (locked && s.Enabled)
                {
                    s.Enabled = false;
                    enabledSrc.Remove(s.Id);
                    _engine.RemoveSource(s.Id);
                }
                s.SetLock(locked, "Está activa como salida (se bloquea para evitar eco)");
            }
            foreach (var o in _outputs)
            {
                bool locked = enabledSrc.Contains(o.Id);
                if (locked && o.Enabled)
                {
                    o.Enabled = false;
                    _engine.RemoveOutput(o.Id);
                }
                o.SetLock(locked, "Está activa como fuente (se bloquea para evitar eco)");
            }
        }
        finally
        {
            _loading = wasLoading;
        }
    }

    // ---------- motor ----------

    private void StartEngineFromUi()
    {
        _engine.RemoveAll();
        _engine.MasterVolume = (float)(MasterSlider.Value / 100.0);
        foreach (var row in _outputs.Where(r => r.Enabled))
            TryAddRow(row);
        foreach (var row in _sources.Where(r => r.Enabled))
            TryAddRow(row);
        UpdateStatus();
    }

    private void TryAddRow(DeviceRow row)
    {
        try
        {
            if (!_devicesById.TryGetValue(row.Id, out var dev)) return;
            if (row.IsOutputRole)
                _engine.AddOutput(dev, (float)(row.Volume / 100.0));
            else
                _engine.AddSource(dev, (float)(row.Volume / 100.0));
        }
        catch (Exception ex)
        {
            bool prev = _loading; _loading = true;
            row.Enabled = false;
            _loading = prev;
            SetStatus($"No se pudo usar «{row.Name}»: {ex.Message}");
        }
    }

    private void UpdateStatus()
    {
        int s = _engine.SourceCount, o = _engine.OutputCount;
        if (s == 0 && o == 0) SetStatus("Marca al menos una fuente y una salida.");
        else if (s == 0) SetStatus($"{o} salida(s) lista(s). Marca alguna fuente para que suene algo.");
        else if (o == 0) SetStatus($"{s} fuente(s) capturada(s). Marca alguna salida para oírlas.");
        else SetStatus($"Mezclando {s} fuente(s) → {o} salida(s), sincronizadas.");
    }

    private void SetStatus(string text) => StatusText.Text = text;

    private void QueueSave()
    {
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    private void SyncConfigFromUi()
    {
        _config.MasterVolume = MasterSlider.Value;
        foreach (var row in _sources)
        {
            var d = AppConfig.GetOrAdd(_config.Sources, row.Id);
            d.Enabled = row.Enabled;
            d.Volume = row.Volume;
        }
        foreach (var row in _outputs)
        {
            var d = AppConfig.GetOrAdd(_config.Outputs, row.Id);
            d.Enabled = row.Enabled;
            d.Volume = row.Volume;
        }
    }

    // ---------- handlers UI ----------

    private void Refresh_Click(object sender, RoutedEventArgs e)
    {
        SyncConfigFromUi();
        _engine.RemoveAll();
        RefreshDevices();
        StartEngineFromUi();
    }

    private void Row_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        if ((sender as FrameworkElement)?.DataContext is not DeviceRow row) return;

        if (row.Enabled) TryAddRow(row);
        else if (row.IsOutputRole) _engine.RemoveOutput(row.Id);
        else _engine.RemoveSource(row.Id);

        UpdateLocks();
        UpdateStatus();
        QueueSave();
    }

    private void RowVolume_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        if ((sender as FrameworkElement)?.DataContext is not DeviceRow row) return;
        float v = (float)(row.Volume / 100.0);
        if (row.IsOutputRole) _engine.SetOutputVolume(row.Id, v);
        else _engine.SetSourceVolume(row.Id, v);
        QueueSave();
    }

    // ---------- cable virtual ----------

    private async void Cable_Click(object sender, RoutedEventArgs e)
    {
        if (_cableAction == CableAction.None) return;

        if (_cableAction == CableAction.Activate)
        {
            var cable = FindCableSource();
            if (cable != null) ActivateCable(cable);
            return;
        }

        CableButton.IsEnabled = false;
        try
        {
            SetStatus("Descargando VB-CABLE desde vb-audio.com…");
            string setup = await CableInstaller.DownloadAsync();
            SetStatus("Instalando el driver — acepta el aviso de UAC…");
            int code = await CableInstaller.InstallAsync(setup);
            SetStatus($"Instalador terminado (código {code}). Buscando CABLE Input…");
            await Task.Delay(2000);
            Refresh_Click(this, e);
            var cable = FindCableSource();
            if (cable != null)
            {
                ActivateCable(cable);
            }
            else
            {
                SetStatus("Driver instalado, pero CABLE Input aún no aparece: reinicia Windows y vuelve a pulsar este botón.");
                MessageBox.Show(
                    "VB-CABLE quedó instalado, pero Windows necesita reiniciarse para que aparezca el dispositivo.\n\n" +
                    "Después de reiniciar, abre Crisol y pulsa \"Activar cable\".",
                    "Crisol", MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }
        catch (OperationCanceledException)
        {
            SetStatus("Instalación cancelada en el UAC.");
        }
        catch (Exception ex)
        {
            SetStatus("Falló la instalación del cable: " + ex.Message);
        }
        finally
        {
            UpdateCableButton();
        }
    }

    /// <summary>
    /// Pone CABLE Input como salida por defecto de Windows (todo el audio entra ahí, mudo),
    /// lo marca como fuente y convierte la antigua salida por defecto en salida de Crisol
    /// para que no se quede sin sonido.
    /// </summary>
    private void ActivateCable(DeviceRow cable)
    {
        string? oldDefault = _defaultRenderId;
        try
        {
            PolicyConfig.SetDefaultDevice(cable.Id);
        }
        catch (Exception ex)
        {
            SetStatus("No pude cambiar la salida por defecto (" + ex.Message +
                      "): ponla a mano en Configuración → Sonido → CABLE Input.");
            return;
        }
        _defaultRenderId = cable.Id;

        _loading = true;
        try
        {
            cable.Enabled = true;
            var oldSrc = _sources.FirstOrDefault(s => s.Id == oldDefault);
            if (oldSrc is { Enabled: true }) oldSrc.Enabled = false;
            var oldOut = _outputs.FirstOrDefault(o => o.Id == oldDefault);
            if (oldOut is { Enabled: false }) oldOut.Enabled = true;
        }
        finally
        {
            _loading = false;
        }
        UpdateLocks();
        StartEngineFromUi();
        QueueSave();
        UpdateCableButton();
        SetStatus($"Cable activo: el audio de Windows entra por CABLE Input y suena por {_engine.OutputCount} salida(s), sincronizadas y sin lag entre ellas.");
    }

    // ---------- prueba de sonido ----------

    private bool _testing;

    private async void Test_Click(object sender, RoutedEventArgs e)
    {
        if (_testing) return;
        _testing = true;
        TestButton.IsEnabled = false;
        try
        {
            double freq = 440;
            foreach (var row in _outputs)
            {
                if (!_devicesById.TryGetValue(row.Id, out var dev)) continue;
                SetStatus($"Tono directo ({freq:F0} Hz) → {row.Name}…");
                try { await PlayToneAsync(dev, freq, 1.5); }
                catch (Exception ex)
                {
                    SetStatus($"Falló el tono en «{row.Name}»: {ex.Message}");
                    await Task.Delay(1500);
                }
                freq += 220;
            }

            int legs = _engine.OutputCount;
            if (legs == 0)
            {
                SetStatus("Tonos directos listos. No hay salidas marcadas: no se probó la mezcla.");
                return;
            }

            long tapBytesBefore = _engine.TotalTapBytes;
            _engine.ResetPeak();
            SetStatus($"Tono por la mezcla de Crisol → {legs} salida(s) marcada(s)…");
            _engine.PlayTestTone(2);
            await Task.Delay(2600);

            bool mixOk = _engine.MixPeak > 0.1f && _engine.PumpAlive;
            string fuentes = _engine.SourceCount == 0
                ? "sin fuentes marcadas"
                : _engine.TotalTapBytes > tapBytesBefore
                    ? $"{_engine.SourceCount} fuente(s) entregando audio"
                    : $"{_engine.SourceCount} fuente(s) marcada(s) pero sin audio ahora (¿está sonando algo ahí?)";
            SetStatus(mixOk
                ? $"Prueba OK: la mezcla sonó en {legs} salida(s); {fuentes}."
                : "La mezcla no produjo señal: revisa %APPDATA%\\Crisol\\error.log y pulsa ⟳.");
        }
        finally
        {
            _testing = false;
            TestButton.IsEnabled = true;
        }
    }

    /// <summary>Tono directo al dispositivo, sin pasar por el motor: prueba solo el hardware.</summary>
    private static async Task PlayToneAsync(MMDevice device, double freq, double seconds)
    {
        using var toneOut = new WasapiOut(device, AudioClientShareMode.Shared, true, 100);
        var sig = new SignalGenerator(48000, 2) { Gain = 0.25, Frequency = freq, Type = SignalGeneratorType.Sin };
        toneOut.Init(new SampleToWaveProvider(sig.Take(TimeSpan.FromSeconds(seconds))));
        toneOut.Play();
        await Task.Delay(TimeSpan.FromSeconds(seconds + 0.4));
    }

    private void Master_Changed(object sender, RoutedEventArgs e)
    {
        if (MasterPct == null) return;
        _engine.MasterVolume = (float)(MasterSlider.Value / 100.0);
        MasterPct.Text = ((int)MasterSlider.Value).ToString();
        if (!_loading) QueueSave();
    }

    // ---------- autoinicio ----------

    private static bool IsAutostartEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath);
        return key?.GetValue(RunValueName) != null;
    }

    private void Autostart_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true)!;
            if (AutostartCheck.IsChecked == true)
                key.SetValue(RunValueName, $"\"{Environment.ProcessPath}\" --tray");
            else
                key.DeleteValue(RunValueName, throwOnMissingValue: false);
        }
        catch (Exception ex)
        {
            SetStatus("No se pudo cambiar el autoinicio: " + ex.Message);
        }
    }

    // ---------- bandeja ----------

    private void SetupTray()
    {
        _tray = new WinForms.NotifyIcon
        {
            Icon = MakeTrayIcon(),
            Visible = true,
            Text = "Crisol — mezclador de audio",
        };
        _tray.DoubleClick += (_, _) => ShowFromTray();
        var menu = new WinForms.ContextMenuStrip();
        menu.Items.Add("Abrir", null, (_, _) => ShowFromTray());
        menu.Items.Add("Salir", null, (_, _) => ExitApp());
        _tray.ContextMenuStrip = menu;
    }

    private static Drawing.Icon MakeTrayIcon()
    {
        var bmp = new Drawing.Bitmap(32, 32);
        using (var g = Drawing.Graphics.FromImage(bmp))
        {
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.Clear(Drawing.Color.Transparent);
            using var bg = new Drawing.SolidBrush(Drawing.Color.FromArgb(232, 133, 59));
            g.FillEllipse(bg, 1, 1, 30, 30);
            using var pen = new Drawing.Pen(Drawing.Color.White, 3);
            g.DrawLine(pen, 10, 10, 10, 22);
            g.DrawLine(pen, 16, 7, 16, 25);
            g.DrawLine(pen, 22, 12, 22, 20);
        }
        return Drawing.Icon.FromHandle(bmp.GetHicon());
    }

    private void ShowFromTray()
    {
        Show();
        WindowState = WindowState.Normal;
        Activate();
    }

    private void ExitApp()
    {
        _exiting = true;
        SyncConfigFromUi();
        _config.Save();
        _tray.Visible = false;
        _tray.Dispose();
        _engine.Dispose();
        Application.Current.Shutdown();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!_exiting)
        {
            e.Cancel = true;
            Hide();
            if (!_config.TrayTipShown)
            {
                _config.TrayTipShown = true;
                _config.Save();
                _tray.ShowBalloonTip(3000, "Crisol",
                    "Sigue mezclando en la bandeja. Clic derecho → Salir para cerrarlo del todo.",
                    WinForms.ToolTipIcon.Info);
            }
            return;
        }
        base.OnClosing(e);
    }
}
