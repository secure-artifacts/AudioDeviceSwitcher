using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace AudioDeviceSwitcher;

public partial class MainWindow : Window
{
    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);
    private const int VK_LBUTTON = 0x01;
    private static bool IsLeftMouseDown() => (GetAsyncKeyState(VK_LBUTTON) & 0x8000) != 0;

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);
    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_NOZORDER = 0x0004;
    private const uint SWP_NOACTIVATE = 0x0010;

    private MiniWindow? _miniWindow;
    private HotkeyService? _hotkeyService;
    private bool _forceClose;
    // Read from the Voicemeeter poll thread to decide whether to push UI updates —
    // a plain field avoids cross-thread access to the IsVisible DependencyProperty.
    private volatile bool _windowShown;
    private string _lastStateSignature = "";
    private string? _selectedPlaybackId;
    private string? _selectedRecordingId;
    private System.Windows.Threading.DispatcherTimer? _peakTimer;
    private MMDevice? _peakPlaybackDevice;
    private MMDevice? _peakRecordingDevice;
    // Keepalive capture: AudioMeterInformation on capture endpoints only updates while
    // some client is actually recording. We open a shared-mode capture and discard the
    // bytes so the meter stays active even when no other app is using the mic.
    private WasapiCapture? _peakRecordingCapture;
    private FrameworkElement? _peakPlaybackTrack;
    private FrameworkElement? _peakPlaybackFill;
    private FrameworkElement? _peakRecordingTrack;
    private FrameworkElement? _peakRecordingFill;
    private double _peakPlaybackDisplayed;
    private double _peakRecordingDisplayed;
    // Per-playback-device volume: one MMDevice per row (for read/write), keyed by device id.
    // Disposed and rebuilt on every LoadDevices(). External volume/mute changes arrive via
    // each device's AudioEndpointVolume.OnVolumeNotification event (no polling).
    private readonly Dictionary<string, MMDevice> _playbackVolumeDevices = new(StringComparer.OrdinalIgnoreCase);
    private bool _suppressVolumeWrite;

    // Carried on each volume Slider's Tag so its handler knows which device + label to update.
    private sealed class VolRef
    {
        public required string Id;
        public required TextBlock Label;
    }
    private FrameworkElement[]? _vmOutputTracks;
    private FrameworkElement[]? _vmOutputFills;
    private FrameworkElement[]? _vmInputTracks;
    private FrameworkElement[]? _vmInputFills;
    private double[]? _vmOutputDisplayed;
    private double[]? _vmInputDisplayed;
    private int _vmOutCount;
    private int _vmInCount;
    private Point _profileDragStart;
    private Guid _profileDragSourceId;
    // Per-strip "user just clicked mute" overrides — the dirty poll may read a stale
    // state right after our write, so we trust this for a short window.
    private readonly Dictionary<int, (bool Muted, DateTime When)> _pendingMuteWrites = new();
    private static readonly TimeSpan PendingMuteWindow = TimeSpan.FromSeconds(2);
    // Same idea for device picks: Voicemeeter's device.name read lags the actual swap
    // by up to a couple seconds, so show the device the user just picked until the
    // readback catches up (or the safety window elapses).
    private readonly Dictionary<(bool IsInput, int Index), (string Name, DateTime When)> _pendingDeviceWrites = new();
    private static readonly TimeSpan PendingDeviceWindow = TimeSpan.FromSeconds(6);
    private VoicemeeterIoState? _lastVoicemeeterState;
    // Last rendered VM signature — lets the locked-mode periodic re-check skip a UI
    // rebuild when nothing actually changed.
    private string _vmRenderSig = "";
    // Slots whose device restore failed (device gone, or Voicemeeter rejected the set):
    // key "S<idx>"/"B<idx>" -> the target we gave up on. Prevents an unrestorable slot
    // from being hammered every poll (which blanks/cycles the device). Cleared when the
    // lock is toggled or the snapshot is recaptured.
    private static readonly Dictionary<string, string> _deviceRestoreGaveUp = new();
    // After a successful restore the device.name readback lags >1s; suppress re-issuing
    // the same set for a short window so EnforceVoicemeeterLock doesn't spam it (and
    // briefly flash the drifted device) on every poll while the swap propagates.
    private static readonly Dictionary<string, (string Target, DateTime When)> _recentRestore = new();
    private static readonly TimeSpan RecentRestoreWindow = TimeSpan.FromSeconds(3);
    // Last device the user picked via the select menu, per slot ("S<idx>"/"B<idx>").
    // Voicemeeter's API can't report which driver is active and the same device is
    // enumerated under MME/WDM/KS with the same name, so without this every matching
    // driver entry would show checked. UI-thread only.
    private static readonly Dictionary<string, (int Type, string Name)> _lastPickedVmDevice = new();

    public MainWindow()
    {
        InitializeComponent();
        ApplySettings();
        SourceInitialized += (_, _) =>
        {
            _hotkeyService = new HotkeyService(this);
            RegisterProfileHotkeys();

            // Restore position in physical pixels via Win32 — works correctly across
            // monitors with different DPI (WPF Left/Top cannot be used reliably here).
            var s = SettingsService.Load();
            if (s.MainWindowLeft is double l && s.MainWindowTop is double t)
            {
                var hwnd = new WindowInteropHelper(this).Handle;
                if (hwnd != IntPtr.Zero)
                    SetWindowPos(hwnd, IntPtr.Zero, (int)l, (int)t, 0, 0,
                        SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE);
            }

            // StartMinimized never fires IsVisibleChanged, so the poll loop that
            // enforces the Voicemeeter mute lock would never start. Kick it off here
            // if the lock was left on across restarts.
            if (s.VoicemeeterMuteLocked) StartVoicemeeterPolling();
        };
        LoadProfiles();
        LoadDevices();
        Closing += (_, e) =>
        {
            SaveSettings();
            if (!_forceClose)
            {
                // X button minimizes to the taskbar instead of closing/hiding.
                // Real exit is only via the tray menu (sets _forceClose).
                e.Cancel = true;
                WindowState = WindowState.Minimized;
            }
        };
        // Only save during user drags (mouse held). DPI-adjust LocationChanged fires
        // without mouse pressed, so it's naturally filtered.
        LocationChanged += (_, _) =>
        {
            if (IsLeftMouseDown()) SaveSettings();
        };
        SizeChanged += (_, _) =>
        {
            if (IsLeftMouseDown()) SaveSettings();
        };
        IsVisibleChanged += (_, _) =>
        {
            _windowShown = IsVisible;
            if (IsVisible)
            {
                _lastStateSignature = "";
                RefreshFromExternalChange();
                StartVoicemeeterPolling();
                if (WindowState != WindowState.Minimized) StartPeakMetering();
            }
            else
            {
                // Keep the poll loop alive while the mute lock is on so external
                // Voicemeeter changes are still reverted after hiding to tray.
                if (!SettingsService.Load().VoicemeeterMuteLocked) StopVoicemeeterPolling();
                StopPeakMetering();
            }
        };
        StateChanged += (_, _) =>
        {
            if (!IsVisible) return;
            if (WindowState == WindowState.Minimized) StopPeakMetering();
            else StartPeakMetering();
        };
    }

    private void StartPeakMetering()
    {
        if (_peakTimer != null) return;
        if (_peakRecordingCapture == null) RefreshPeakDevices();
        _peakTimer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(50),
        };
        _peakTimer.Tick += PeakTimer_Tick;
        _peakTimer.Start();
    }

    private void StopPeakMetering()
    {
        _peakTimer?.Stop();
        _peakTimer = null;
        _peakPlaybackDisplayed = 0;
        _peakRecordingDisplayed = 0;
        StopRecordingKeepalive();
    }

    private void RefreshPeakDevices()
    {
        StopRecordingKeepalive();
        try { _peakPlaybackDevice?.Dispose(); } catch { }
        try { _peakRecordingDevice?.Dispose(); } catch { }
        _peakPlaybackDevice = null;
        _peakRecordingDevice = null;
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            try { _peakPlaybackDevice = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia); } catch { }
            try { _peakRecordingDevice = enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Multimedia); } catch { }
        }
        catch { }

        if (_peakRecordingDevice != null)
        {
            try
            {
                var cap = new WasapiCapture(_peakRecordingDevice) { ShareMode = AudioClientShareMode.Shared };
                cap.DataAvailable += (_, _) => { };
                cap.StartRecording();
                _peakRecordingCapture = cap;
            }
            catch
            {
                try { _peakRecordingCapture?.Dispose(); } catch { }
                _peakRecordingCapture = null;
            }
        }
    }

    private void StopRecordingKeepalive()
    {
        if (_peakRecordingCapture == null) return;
        try { _peakRecordingCapture.StopRecording(); } catch { }
        try { _peakRecordingCapture.Dispose(); } catch { }
        _peakRecordingCapture = null;
    }

    private void PeakTimer_Tick(object? sender, EventArgs e)
    {
        UpdatePeak(_peakPlaybackDevice, _peakPlaybackTrack, _peakPlaybackFill, ref _peakPlaybackDisplayed);
        UpdatePeak(_peakRecordingDevice, _peakRecordingTrack, _peakRecordingFill, ref _peakRecordingDisplayed);
        UpdateVoicemeeterPeaks();
    }

    // Fired by Windows (on a system thread) when a device's volume/mute changes from any source.
    // Marshals to the UI thread and updates that row's controls. Skips the slider while the user
    // is dragging it; _suppressVolumeWrite stops the write-back from echoing into the device.
    private void OnDeviceVolumeChanged(Slider slider, TextBlock pct, Button muteBtn, AudioVolumeNotificationData data)
    {
        Dispatcher.BeginInvoke(() =>
        {
            ApplyMuteGlyph(muteBtn, data.Muted);
            if (!slider.IsMouseCaptureWithin && Math.Abs(slider.Value - data.MasterVolume) > 0.005)
            {
                _suppressVolumeWrite = true;
                slider.Value = data.MasterVolume;
                _suppressVolumeWrite = false;
            }
            pct.Text = $"{(int)Math.Round(data.MasterVolume * 100)}%";
        });
    }

    private void DisposePlaybackVolumeDevices()
    {
        foreach (var d in _playbackVolumeDevices.Values)
            try { d.Dispose(); } catch { }
        _playbackVolumeDevices.Clear();
    }

    // Mute button + volume slider + percent label for one playback device row.
    // Holds the device's MMDevice in _playbackVolumeDevices and subscribes to its
    // OnVolumeNotification so external volume/mute changes update the row live.
    private Grid BuildVolumeRow(string deviceId)
    {
        var muteGlyph = new TextBlock
        {
            FontFamily = new FontFamily("Segoe MDL2 Assets"),
            FontSize = 14,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var muteBtn = new Button
        {
            Content = muteGlyph,
            Tag = deviceId,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(2, 0, 2, 0),
            Cursor = Cursors.Hand,
            Focusable = false,
            VerticalAlignment = VerticalAlignment.Center,
        };
        muteBtn.Click += MuteButton_Click;

        var pct = new TextBlock
        {
            Width = 34,
            TextAlignment = TextAlignment.Right,
            FontSize = 11,
            Foreground = new SolidColorBrush(Color.FromRgb(0x6B, 0x72, 0x80)),
            VerticalAlignment = VerticalAlignment.Center,
        };

        var slider = new Slider
        {
            Minimum = 0,
            Maximum = 1,
            SmallChange = 0.02,
            LargeChange = 0.1,
            Focusable = false,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(4, 0, 6, 0),
            Tag = new VolRef { Id = deviceId, Label = pct },
        };
        slider.ValueChanged += VolumeSlider_ValueChanged;

        var grid = new Grid { Margin = new Thickness(0, 4, 0, 0) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(muteBtn, 0);
        Grid.SetColumn(slider, 1);
        Grid.SetColumn(pct, 2);
        grid.Children.Add(muteBtn);
        grid.Children.Add(slider);
        grid.Children.Add(pct);

        // Acquire the device and seed initial values.
        float vol = 1f;
        bool muted = false;
        var dev = AudioDeviceService.GetDeviceById(deviceId);
        if (dev != null)
        {
            _playbackVolumeDevices[deviceId] = dev;
            try
            {
                var v = dev.AudioEndpointVolume;
                vol = v.MasterVolumeLevelScalar;
                muted = v.Mute;
                v.OnVolumeNotification += data => OnDeviceVolumeChanged(slider, pct, muteBtn, data);
            }
            catch (COMException) { }
        }
        else
        {
            slider.IsEnabled = false;
            muteBtn.IsEnabled = false;
        }

        _suppressVolumeWrite = true;
        slider.Value = vol;
        _suppressVolumeWrite = false;
        pct.Text = $"{(int)Math.Round(vol * 100)}%";
        ApplyMuteGlyph(muteBtn, muted);
        return grid;
    }

    private static void ApplyMuteGlyph(Button btn, bool muted)
    {
        if (btn.Content is not TextBlock tb) return;
        tb.Text = muted ? "" : ""; // Segoe MDL2: Mute / Volume
        tb.Foreground = new SolidColorBrush(muted
            ? Color.FromRgb(0xB9, 0x1C, 0x1C)
            : Color.FromRgb(0x37, 0x41, 0x51));
        btn.ToolTip = muted ? "已静音 — 点击取消" : "点击静音";
    }

    private void VolumeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_suppressVolumeWrite) return;
        if (sender is not Slider s || s.Tag is not VolRef r) return;
        if (_playbackVolumeDevices.TryGetValue(r.Id, out var dev))
        {
            try { dev.AudioEndpointVolume.MasterVolumeLevelScalar = (float)e.NewValue; }
            catch (COMException) { }
        }
        r.Label.Text = $"{(int)Math.Round(e.NewValue * 100)}%";
    }

    private void MuteButton_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true; // don't bubble to DeviceButton_Click (it rebuilds the whole list)
        if (sender is not Button b || b.Tag is not string id) return;
        if (!_playbackVolumeDevices.TryGetValue(id, out var dev)) return;
        try
        {
            var v = dev.AudioEndpointVolume;
            v.Mute = !v.Mute;
            ApplyMuteGlyph(b, v.Mute);
        }
        catch (COMException) { }
    }

    private void UpdateVoicemeeterPeaks()
    {
        if (_vmOutCount == 0 && _vmInCount == 0) return;
        if (_vmOutputTracks == null || _vmOutputFills == null
            || _vmInputTracks == null || _vmInputFills == null
            || _vmOutputDisplayed == null || _vmInputDisplayed == null) return;
        var (outs, ins) = VoicemeeterService.GetPeakLevels(_vmOutCount, _vmInCount);
        for (int i = 0; i < outs.Length && i < _vmOutputTracks.Length; i++)
            UpdateVmBar(_vmOutputTracks[i], _vmOutputFills[i], outs[i], ref _vmOutputDisplayed[i]);
        for (int i = 0; i < ins.Length && i < _vmInputTracks.Length; i++)
            UpdateVmBar(_vmInputTracks[i], _vmInputFills[i], ins[i], ref _vmInputDisplayed[i]);
    }

    private static void UpdateVmBar(FrameworkElement track, FrameworkElement fill, float peak, ref double displayed)
    {
        double clamped = Math.Min(1.0, peak);
        displayed = clamped >= displayed ? clamped : Math.Max(clamped, displayed * 0.85);
        double w = track.ActualWidth * displayed;
        if (double.IsNaN(w) || w < 0) w = 0;
        fill.Width = w;
    }

    private static void UpdatePeak(MMDevice? device, FrameworkElement? track, FrameworkElement? fill, ref double displayed)
    {
        if (track == null || fill == null) return;
        float peak = 0f;
        if (device != null)
        {
            try { peak = device.AudioMeterInformation.MasterPeakValue; } catch { peak = 0f; }
        }
        // Visual smoothing: rises instantly, decays gradually.
        displayed = peak >= displayed ? peak : Math.Max(peak, displayed * 0.85);
        double w = track.ActualWidth * displayed;
        if (double.IsNaN(w) || w < 0) w = 0;
        fill.Width = w;
    }

    // Background thread tight-poll: 50ms when locked (so external Voicemeeter UI changes
    // are reverted in <100ms), 1.5s otherwise. IsParametersDirty is a microsecond-cheap
    // memory read so this is essentially free.
    private CancellationTokenSource? _voicemeeterPollCts;
    private Thread? _voicemeeterPollThread;

    private void StartVoicemeeterPolling()
    {
        if (_voicemeeterPollThread != null) return;
        _voicemeeterPollCts = new CancellationTokenSource();
        var token = _voicemeeterPollCts.Token;
        _voicemeeterPollThread = new Thread(() => VoicemeeterPollLoop(token))
        {
            IsBackground = true,
            Name = "VoicemeeterPoll",
        };
        _voicemeeterPollThread.Start();
    }

    private void StopVoicemeeterPolling()
    {
        try { _voicemeeterPollCts?.Cancel(); } catch { }
        _voicemeeterPollCts?.Dispose();
        _voicemeeterPollCts = null;
        _voicemeeterPollThread = null;
    }

    private void VoicemeeterPollLoop(CancellationToken token)
    {
        var sw = Stopwatch.StartNew();
        long lastFullMs = -100000;
        while (!token.IsCancellationRequested)
        {
            bool locked = false;
            try
            {
                locked = SettingsService.Load().VoicemeeterMuteLocked;
                bool dirty = VoicemeeterService.IsParametersDirty();
                // When locked, re-evaluate on a steady cadence even without a dirty
                // edge: device.name lags the swap by ~1s, so an external change is
                // usually NOT yet visible on the single poll its dirty flag triggers,
                // and the flag is consumed before the readback catches up.
                bool forced = locked && (sw.ElapsedMilliseconds - lastFullMs) >= 750;
                if (dirty || forced)
                {
                    lastFullMs = sw.ElapsedMilliseconds;
                    var state = VoicemeeterService.GetIoState();
                    if (state != null) state = EnforceVoicemeeterLock(state);
                    // Enforcement runs regardless; only rebuild UI when it's visible
                    // and something actually changed (avoid 750ms flicker/rebuild).
                    var sig = VmSignature(state);
                    if (_windowShown && (dirty || sig != _vmRenderSig))
                        Dispatcher.BeginInvoke(() => RenderVoicemeeterStatus(state));
                    _vmRenderSig = sig;
                }
            }
            catch { }
            int interval = locked ? 50 : 1500;
            if (token.WaitHandle.WaitOne(interval)) break;
        }
    }

    private void ApplySettings()
    {
        // Position is restored in SourceInitialized via Win32 (physical pixels).
        // Only size is restored here — it's in DIPs and behaves consistently.
        var s = SettingsService.Load();
        WindowStartupLocation = WindowStartupLocation.Manual;
        if (s.MainWindowWidth is double w && w >= MinWidth) Width = w;
        if (s.MainWindowHeight is double h && h >= MinHeight) Height = h;
        ShowHiddenCheck.IsChecked = s.ShowHiddenDevices;
        ShowDisabledCheck.IsChecked = s.ShowDisabledDevices;
    }

    private void SaveSettings()
    {
        if (WindowState != WindowState.Normal) return;

        var s = SettingsService.Load();
        // Save position in physical pixels via Win32 — bypasses WPF DIP ambiguity
        // across multi-DPI monitors.
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd != IntPtr.Zero && GetWindowRect(hwnd, out var rect))
        {
            s.MainWindowLeft = rect.Left;
            s.MainWindowTop = rect.Top;
        }
        if (IsValid(Width)) s.MainWindowWidth = Width;
        if (IsValid(Height)) s.MainWindowHeight = Height;
        SettingsService.Save();
    }

    private static bool IsValid(double v) => !double.IsNaN(v) && !double.IsInfinity(v);

    private static bool IsOnScreen(double left, double top)
    {
        // Virtual screen bounds in DIPs (matches WPF Left/Top units under PerMonitorV2).
        var vLeft = SystemParameters.VirtualScreenLeft;
        var vTop = SystemParameters.VirtualScreenTop;
        var vRight = vLeft + SystemParameters.VirtualScreenWidth;
        var vBottom = vTop + SystemParameters.VirtualScreenHeight;
        return left >= vLeft - 50 && left <= vRight - 50
            && top >= vTop - 10 && top <= vBottom - 50;
    }

    public void OpenMiniWindow()
    {
        if (_miniWindow is { IsLoaded: true })
        {
            _miniWindow.Activate();
            return;
        }

        _miniWindow = new MiniWindow(() =>
        {
            LoadDevices();
            LoadProfiles();
        });
        _miniWindow.Closed += (_, _) =>
        {
            _miniWindow = null;
            UpdateMiniMenuCheck();
        };
        _miniWindow.Show();

        var settings = SettingsService.Load();
        settings.MiniWindowVisible = true;
        SettingsService.Save();

        UpdateMiniMenuCheck();
    }

    private void UpdateMiniMenuCheck()
    {
        if (MiniWindowMenuItem == null) return;
        bool open = _miniWindow is { IsLoaded: true };
        MiniWindowMenuItem.IsChecked = open;
        MiniWindowMenuItem.Header = open ? "\u5173\u95ED\u8FF7\u4F60\u7A97\u53E3" : "\u6253\u5F00\u8FF7\u4F60\u7A97\u53E3";
    }

    public void ForceClose()
    {
        _forceClose = true;
        _hotkeyService?.Dispose();
        _miniWindow?.Close();
        Close();
    }

    public void RefreshFromExternalChange()
    {
        // Nothing to refresh if both windows are hidden/closed
        if (!IsVisible && _miniWindow == null) return;

        // Skip if state hasn't changed (avoid needless UI rebuild)
        var signature = ComputeStateSignature();
        if (signature == _lastStateSignature) return;
        _lastStateSignature = signature;

        if (IsVisible)
        {
            LoadDevices();
            LoadProfiles();
        }
        _miniWindow?.LoadProfiles();
    }

    private static string ComputeStateSignature()
    {
        var allPlayback = AudioDeviceService.GetPlaybackDevices(true);
        var allRecording = AudioDeviceService.GetRecordingDevices(true);
        var playback = allPlayback.Find(d => d.IsDefault);
        var recording = allRecording.Find(d => d.IsDefault);
        var playbackComm = AudioDeviceService.GetCommunicationsDefault(NAudio.CoreAudioApi.DataFlow.Render);
        var recordingComm = AudioDeviceService.GetCommunicationsDefault(NAudio.CoreAudioApi.DataFlow.Capture);
        var bt = AudioDeviceService.HasBluetoothDevice();
        var profileCount = ProfileService.GetAll().Count;
        var drift = string.Join(",", ((App)Application.Current).DriftedApps);
        // Fingerprint every device's id+state so external enable/disable/add/remove triggers refresh.
        var deviceFp = string.Join(",",
            allPlayback.Concat(allRecording).Select(d => $"{d.Id}:{(d.IsDisabled ? "D" : "A")}"));
        return $"{playback?.Id}|{recording?.Id}|{playbackComm.Id}|{recordingComm.Id}|{bt}|{profileCount}|{drift}|{deviceFp}";
    }

    private void RegisterProfileHotkeys()
    {
        _hotkeyService?.UnregisterAll();
        foreach (var profile in ProfileService.GetAll())
        {
            if (profile.HotkeyKey == 0) continue;
            _hotkeyService?.Register(profile.HotkeyModifiers, profile.HotkeyKey, () =>
            {
                try
                {
                    if (!((App)Application.Current).TryUserApplyProfile(profile)) return;
                    var result = ProfileApplyService.Apply(profile);
                    ((App)Application.Current).MarkOwnChange();
                    LoadDevices();
                    LoadProfiles();
                    _miniWindow?.LoadProfiles();
                    ((App)Application.Current).NotifyProfileApplied(profile, result);
                }
                catch { }
            });
        }
    }

    private void ToggleMiniWindow_Click(object sender, RoutedEventArgs e)
    {
        if (_miniWindow is { IsLoaded: true })
        {
            _miniWindow.Close();
            _miniWindow = null;
            var s = SettingsService.Load();
            s.MiniWindowVisible = false;
            SettingsService.Save();
            UpdateMiniMenuCheck();
        }
        else
        {
            OpenMiniWindow();
        }
    }

    // ── Devices ──────────────────────────────────────────────

    private void LoadDevices()
    {
        // Drop stale references — buttons get rebuilt below and may not include the bar.
        _peakPlaybackTrack = null;
        _peakPlaybackFill = null;
        _peakRecordingTrack = null;
        _peakRecordingFill = null;
        _peakPlaybackDisplayed = 0;
        _peakRecordingDisplayed = 0;
        DisposePlaybackVolumeDevices();

        LoadPlaybackDevices();
        LoadRecordingDevices();
        RefreshPeakDevices();
        BluetoothWarning.Visibility = AudioDeviceService.HasBluetoothDevice()
            ? Visibility.Visible : Visibility.Collapsed;
        RefreshVoicemeeterStatus();
    }

    // Off-thread to keep the UI responsive: VBVMR_Login + GetParameterStringA serialized
    // through Voicemeeter's IPC takes ~100ms even when the app is local.
    private void RefreshVoicemeeterStatus()
    {
        Task.Run(() =>
        {
            VoicemeeterIoState? state = null;
            try { state = VoicemeeterService.GetIoState(); } catch { }
            if (state != null) state = EnforceVoicemeeterLock(state);
            Dispatcher.BeginInvoke(() => RenderVoicemeeterStatus(state));
        });
    }

    // When the lock is on, push our snapshot back to the engine for any Strip mute or
    // Strip/Bus device routing that has drifted, then patch the returned state so the UI
    // doesn't briefly flash the drifted value.
    private static VoicemeeterIoState EnforceVoicemeeterLock(VoicemeeterIoState state)
    {
        var settings = SettingsService.Load();
        if (!settings.VoicemeeterMuteLocked) return state;

        // Legacy / restarted lock: the lock was turned on before this feature existed
        // (or persisted across a restart) so the device snapshot is empty. Capture the
        // current routing once instead of forcing the user to unlock + re-lock.
        if (settings.VoicemeeterStripDeviceSnapshot.Count == 0
            && settings.VoicemeeterBusDeviceSnapshot.Count == 0
            && (state.Inputs.Count > 0 || state.Outputs.Count > 0))
        {
            settings.VoicemeeterStripDeviceSnapshot = state.Inputs.Select(s => s.DeviceName ?? "").ToList();
            settings.VoicemeeterBusDeviceSnapshot = state.Outputs.Select(s => s.DeviceName ?? "").ToList();
            if (settings.VoicemeeterStripMuteSnapshot.Count == 0)
                settings.VoicemeeterStripMuteSnapshot = state.Inputs.Select(s => s.Muted).ToList();
            SettingsService.Save();
            ResetDeviceRestoreState();
            VoicemeeterService.LogExternal("EnforceVoicemeeterLock: auto-captured device snapshot "
                + $"(strips=[{string.Join(" | ", settings.VoicemeeterStripDeviceSnapshot)}] "
                + $"buses=[{string.Join(" | ", settings.VoicemeeterBusDeviceSnapshot)}])");
        }

        var muteSnap = settings.VoicemeeterStripMuteSnapshot;
        var stripDevSnap = settings.VoicemeeterStripDeviceSnapshot;
        var busDevSnap = settings.VoicemeeterBusDeviceSnapshot;

        bool inputsChanged = false;
        var newInputs = new List<VoicemeeterIoSlot>(state.Inputs.Count);
        for (int i = 0; i < state.Inputs.Count; i++)
        {
            var slot = state.Inputs[i];

            if (muteSnap != null && i < muteSnap.Count && slot.Muted != muteSnap[i])
            {
                try { VoicemeeterService.SetStripMute(slot.Index, muteSnap[i]); } catch { }
                slot = slot with { Muted = muteSnap[i] };
                inputsChanged = true;
            }

            // Empty snapshot entry = slot had no device when locked; don't force-clear.
            // Virtual inputs have no hardware device to restore — only enforce their mute.
            if (stripDevSnap != null && i < stripDevSnap.Count
                && !string.IsNullOrEmpty(stripDevSnap[i])
                && !slot.IsVirtual
                && !DeviceNameMatches(slot.DeviceName, stripDevSnap[i])
                && TryRestoreLockedDevice(true, slot.Index, stripDevSnap[i]))
            {
                // Show the intended locked device; it's the user's pick, not "missing" —
                // the red flag here reflected the (transient) drifted device, not this one.
                slot = slot with { DeviceName = stripDevSnap[i], DeviceMissing = false };
                inputsChanged = true;
            }

            newInputs.Add(slot);
        }

        bool outputsChanged = false;
        var newOutputs = new List<VoicemeeterIoSlot>(state.Outputs.Count);
        for (int i = 0; i < state.Outputs.Count; i++)
        {
            var slot = state.Outputs[i];
            if (busDevSnap != null && i < busDevSnap.Count
                && !string.IsNullOrEmpty(busDevSnap[i])
                && !DeviceNameMatches(slot.DeviceName, busDevSnap[i])
                && TryRestoreLockedDevice(false, slot.Index, busDevSnap[i]))
            {
                slot = slot with { DeviceName = busDevSnap[i], DeviceMissing = false };
                outputsChanged = true;
            }
            newOutputs.Add(slot);
        }

        if (inputsChanged) state = state with { Inputs = newInputs };
        if (outputsChanged) state = state with { Outputs = newOutputs };
        return state;
    }

    // Restore one locked Strip/Bus device, but never retry a slot we already failed to
    // restore to the same target — retrying blanks/cycles the device every poll.
    private static bool TryRestoreLockedDevice(bool isInput, int hwIndex, string target)
    {
        string key = (isInput ? "S" : "B") + hwIndex;
        lock (_deviceRestoreGaveUp)
        {
            if (_deviceRestoreGaveUp.TryGetValue(key, out var failed) && failed == target)
                return false; // gave up on this exact target; wait for a change / re-lock
            if (_recentRestore.TryGetValue(key, out var recent) && recent.Target == target
                && DateTime.UtcNow - recent.When < RecentRestoreWindow)
                return true; // just restored; swap still propagating — don't re-issue
        }

        bool ok = false;
        try { ok = VoicemeeterService.RestoreIoDevice(isInput, hwIndex, target); } catch { }

        lock (_deviceRestoreGaveUp)
        {
            if (ok)
            {
                _deviceRestoreGaveUp.Remove(key);
                _recentRestore[key] = (target, DateTime.UtcNow);
                return true;
            }
            bool firstTime = !(_deviceRestoreGaveUp.TryGetValue(key, out var prev) && prev == target);
            _deviceRestoreGaveUp[key] = target;
            if (firstTime)
                VoicemeeterService.LogExternal(
                    $"device lock: giving up {(isInput ? "Strip" : "Bus")}[{hwIndex}] -> '{target}' "
                    + "(not retried until it changes or you re-lock)");
            return false;
        }
    }

    // Forget all give-up state — call when the lock toggles or the snapshot is recaptured.
    private static void ResetDeviceRestoreState()
    {
        lock (_deviceRestoreGaveUp) { _deviceRestoreGaveUp.Clear(); _recentRestore.Clear(); }
    }

    // Voicemeeter MME names get truncated (~31 chars); prefix-match both ways so we
    // don't keep re-restoring a slot that's actually already on the right device.
    private static bool DeviceNameMatches(string? current, string snapshot)
    {
        if (string.IsNullOrEmpty(current)) return false;
        if (current.Equals(snapshot, StringComparison.OrdinalIgnoreCase)) return true;
        // Prefix match for MME's truncated names, but a " [A]"/" [B]" disambiguator suffix
        // marks a DIFFERENT device ("Headset" vs "Headset [A]") — don't conflate them, or
        // drift detection and lock restore target the wrong device.
        return IsTruncationPrefix(current, snapshot) || IsTruncationPrefix(snapshot, current);
    }

    private static bool IsTruncationPrefix(string longer, string shorter)
    {
        if (!longer.StartsWith(shorter, StringComparison.OrdinalIgnoreCase)) return false;
        var extra = longer.Substring(shorter.Length).TrimStart();
        return !extra.StartsWith("[", StringComparison.Ordinal);
    }

    // Optimistic device name: while a just-picked device hasn't propagated to the
    // Voicemeeter readback yet, show the pick. Clears once the readback catches up.
    private (string? Name, bool Overridden) EffectiveDevice(bool isInput, VoicemeeterIoSlot slot)
    {
        var k = (isInput, slot.Index);
        if (_pendingDeviceWrites.TryGetValue(k, out var pending))
        {
            if (DeviceNameMatches(slot.DeviceName, pending.Name)
                || DateTime.UtcNow - pending.When >= PendingDeviceWindow)
                _pendingDeviceWrites.Remove(k);
            else
                return (pending.Name, true);
        }
        return (slot.DeviceName, false);
    }

    private static string VmSignature(VoicemeeterIoState? s)
    {
        if (s == null) return "null";
        var sb = new System.Text.StringBuilder(s.TypeName);
        foreach (var o in s.Outputs) sb.Append('|').Append(o.DeviceName);
        foreach (var i in s.Inputs) sb.Append('|').Append(i.DeviceName).Append(i.Muted ? '1' : '0');
        return sb.ToString();
    }

    private void RenderVoicemeeterStatus(VoicemeeterIoState? state)
    {
        VoicemeeterIoList.Items.Clear();
        _vmOutputTracks = null; _vmOutputFills = null;
        _vmInputTracks = null; _vmInputFills = null;
        _vmOutputDisplayed = null; _vmInputDisplayed = null;
        _vmOutCount = 0; _vmInCount = 0;
        _lastVoicemeeterState = state;

        if (state == null)
        {
            VoicemeeterPanel.Visibility = Visibility.Collapsed;
            return;
        }
        VoicemeeterPanel.Visibility = Visibility.Visible;
        VoicemeeterTypeText.Text = state.TypeName;
        UpdateVoicemeeterLockButton();

        _vmOutCount = state.Outputs.Count;
        // Peak meters cover only the hardware strips (virtual inputs show no level bar and
        // their 8-ch level layout would break the simple 2-ch-per-strip read).
        _vmInCount = state.Inputs.Count(s => !s.IsVirtual);
        _vmOutputTracks = new FrameworkElement[_vmOutCount];
        _vmOutputFills = new FrameworkElement[_vmOutCount];
        _vmInputTracks = new FrameworkElement[_vmInCount];
        _vmInputFills = new FrameworkElement[_vmInCount];
        _vmOutputDisplayed = new double[_vmOutCount];
        _vmInputDisplayed = new double[_vmInCount];

        for (int i = 0; i < state.Outputs.Count; i++)
        {
            var slot = state.Outputs[i];
            var (odn, oov) = EffectiveDevice(false, slot);
            var (row, track, fill) = BuildVoicemeeterRow($"▶ {FormatSlotLabel(slot)}", VoicemeeterService.DisplayNameFor(odn, false), false, false, slot.Index, slot.DeviceMissing && !oov);
            VoicemeeterIoList.Items.Add(row);
            if (track != null && fill != null) { _vmOutputTracks[i] = track; _vmOutputFills[i] = fill; }
        }
        for (int i = 0; i < state.Inputs.Count; i++)
        {
            var slot = state.Inputs[i];
            bool effectiveMuted = slot.Muted;
            if (_pendingMuteWrites.TryGetValue(slot.Index, out var pending))
            {
                if (DateTime.UtcNow - pending.When < PendingMuteWindow)
                    effectiveMuted = pending.Muted;
                else
                    _pendingMuteWrites.Remove(slot.Index);
            }
            var (idn, iov) = EffectiveDevice(true, slot);
            var (row, track, fill) = BuildVoicemeeterRow($"● {FormatSlotLabel(slot)}", VoicemeeterService.DisplayNameFor(idn, true), effectiveMuted, true, slot.Index, slot.DeviceMissing && !iov, slot.IsVirtual);
            VoicemeeterIoList.Items.Add(row);
            // Virtual strips have no level bar; only hardware strips (which come first) map
            // to the peak-meter arrays.
            if (!slot.IsVirtual && i < _vmInputTracks.Length && track != null && fill != null)
            {
                _vmInputTracks[i] = track;
                _vmInputFills[i] = fill;
            }
        }
    }

    private static string FormatSlotLabel(VoicemeeterIoSlot slot) =>
        string.IsNullOrEmpty(slot.CustomLabel) ? slot.Label : slot.CustomLabel;

    private (UIElement Row, FrameworkElement? Track, FrameworkElement? Fill) BuildVoicemeeterRow(
        string label, string? deviceName, bool muted, bool isInput, int hwIndex, bool deviceMissing, bool isVirtual = false)
    {
        var stack = new StackPanel { Margin = new Thickness(0, 1, 0, 2) };

        var dock = new DockPanel();
        var labelText = new TextBlock
        {
            Text = label,
            FontSize = 11,
            FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(Color.FromRgb(0x5B, 0x21, 0xB6)),
            MinWidth = 64,
            Margin = new Thickness(0, 0, 8, 0),
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
        };
        DockPanel.SetDock(labelText, Dock.Left);
        dock.Children.Add(labelText);

        var devText = new TextBlock
        {
            Text = string.IsNullOrEmpty(deviceName) ? "(未设置)" : deviceName,
            FontSize = 11,
            Foreground = new SolidColorBrush(string.IsNullOrEmpty(deviceName)
                ? Color.FromRgb(0x9C, 0xA3, 0xAF)
                : deviceMissing
                    ? Color.FromRgb(0xB9, 0x1C, 0x1C)
                    : Color.FromRgb(0x37, 0x41, 0x51)),
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
            Cursor = isVirtual ? Cursors.Arrow : Cursors.Hand,
            ToolTip = isVirtual
                ? "Voicemeeter 虚拟输入（无硬件设备可选）"
                : "点击选择设备（Voicemeeter 中的设备列表）",
        };
        // Virtual inputs have no hardware device to pick — leave the name static.
        if (!isVirtual)
        {
            devText.Tag = (isInput, hwIndex);
            devText.MouseLeftButtonUp += DeviceName_Click;
        }

        if (isInput)
        {
            var muteBadge = new Border
            {
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(3),
                Padding = new Thickness(5, 0, 5, 0),
                Margin = new Thickness(0, 0, 6, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Cursor = Cursors.Hand,
                Child = new TextBlock { Text = "静音", FontSize = 10 },
            };
            muteBadge.Tag = (hwIndex, muted, devText);
            ApplyMuteVisual(muteBadge, devText, muted);
            muteBadge.MouseLeftButtonUp += MuteBadge_Click;
            DockPanel.SetDock(muteBadge, Dock.Left);
            dock.Children.Add(muteBadge);
        }

        if (deviceMissing)
        {
            var missingBadge = new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(0xFE, 0xE2, 0xE2)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(0xFC, 0xA5, 0xA5)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(3),
                Padding = new Thickness(5, 0, 5, 0),
                Margin = new Thickness(0, 0, 6, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Child = new TextBlock
                {
                    Text = "已丢失",
                    FontSize = 10,
                    FontWeight = FontWeights.SemiBold,
                    Foreground = new SolidColorBrush(Color.FromRgb(0xB9, 0x1C, 0x1C)),
                },
                ToolTip = "Voicemeeter 配置的设备在系统中找不到",
            };
            DockPanel.SetDock(missingBadge, Dock.Left);
            dock.Children.Add(missingBadge);
        }

        dock.Children.Add(devText);
        stack.Children.Add(dock);

        // No level bar for virtual inputs (per design — their 8-ch level layout differs).
        if (isVirtual)
            return (stack, null, null);

        var fill = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(0x7C, 0x3A, 0xED)),
            CornerRadius = new CornerRadius(1.5),
            HorizontalAlignment = HorizontalAlignment.Left,
            Width = 0,
        };
        var track = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(0xDD, 0xD6, 0xFE)),
            CornerRadius = new CornerRadius(1.5),
            Height = 3,
            Margin = new Thickness(64, 2, 2, 0),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Child = fill,
            ClipToBounds = true,
        };
        stack.Children.Add(track);

        return (stack, track, fill);
    }

    private void MuteBadge_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is not Border b) return;
        if (b.Tag is not ValueTuple<int, bool, TextBlock> tag) return;
        var (idx, wasMuted, devText) = tag;
        bool newMuted = !wasMuted;
        if (!VoicemeeterService.SetStripMute(idx, newMuted)) return;
        // Optimistic update + record latest intent so any in-flight dirty poll that
        // reads stale state won't visually revert us.
        _pendingMuteWrites[idx] = (newMuted, DateTime.UtcNow);
        ApplyMuteVisual(b, devText, newMuted);
        b.Tag = (idx, newMuted, devText);
        // If the Voicemeeter is locked, our intent becomes the new snapshot value —
        // otherwise the next poll would revert the change we just made via the badge.
        var settings = SettingsService.Load();
        if (settings.VoicemeeterMuteLocked && idx >= 0 && idx < settings.VoicemeeterStripMuteSnapshot.Count)
        {
            settings.VoicemeeterStripMuteSnapshot[idx] = newMuted;
            SettingsService.Save();
        }
    }

    // Driver display order in the menu — same priority as restore (WDM, KS, MME, ASIO).
    private static int DriverOrder(int type) => type switch { 3 => 0, 4 => 1, 1 => 2, 5 => 3, _ => 9 };

    private void DeviceName_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is not TextBlock tb || tb.Tag is not ValueTuple<bool, int> tag) return;
        var (isInput, hwIndex) = tag;

        string? currentName = null;
        var st = _lastVoicemeeterState;
        if (st != null)
            foreach (var s in (isInput ? st.Inputs : st.Outputs))
                if (s.Index == hwIndex) { currentName = s.DeviceName; break; }

        // Enumeration is a Voicemeeter IPC round-trip — off the UI thread, then show menu.
        Task.Run(() =>
        {
            List<VoicemeeterDevice> devs;
            try { devs = VoicemeeterService.GetSelectableDevices(isInput); } catch { devs = []; }
            Dispatcher.BeginInvoke(() =>
            {
                if (devs.Count == 0)
                {
                    ((App)Application.Current).ShowBalloon("Voicemeeter", "未获取到可选设备", true);
                    return;
                }
                var ordered = devs
                    .OrderBy(x => DriverOrder(x.Type))
                    .ThenBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
                    .ToList();

                // Decide the single item to check. Voicemeeter only tells us the device
                // NAME, not the driver, and the same device appears under several
                // drivers — so prefer the driver the user last picked here (if it's
                // still the active device), else the highest-priority name match.
                string key = (isInput ? "S" : "B") + hwIndex;

                // First menu open after a restart: seed the remembered pick from settings.
                if (!_lastPickedVmDevice.ContainsKey(key)
                    && SettingsService.Load().VoicemeeterDevicePicks.TryGetValue(key, out var saved))
                {
                    int bar = saved.IndexOf('|');
                    if (bar > 0 && int.TryParse(saved.Substring(0, bar), out var savedType))
                        _lastPickedVmDevice[key] = (savedType, saved.Substring(bar + 1));
                }

                VoicemeeterDevice? checkedDev = null;
                if (currentName != null)
                {
                    // 1. The exact driver+device the user last picked here (disambiguates
                    //    which driver shows the same device), if it's still the current one.
                    if (_lastPickedVmDevice.TryGetValue(key, out var picked)
                        && DeviceNameMatches(currentName, picked.Name))
                        foreach (var d in ordered)
                            if (d.Type == picked.Type
                                && string.Equals(d.Name, picked.Name, StringComparison.OrdinalIgnoreCase))
                            { checkedDev = d; break; }

                    // 2. Exact name match — so "Headphones [B]" (BX17) isn't stolen by the
                    //    generic "Headphones" (a different device) through loose prefix matching.
                    if (checkedDev == null)
                        foreach (var d in ordered)
                            if (string.Equals(d.Name, currentName, StringComparison.OrdinalIgnoreCase))
                            { checkedDev = d; break; }

                    // 3. Prefix match as a last resort — covers MME's ~31-char truncated names.
                    if (checkedDev == null)
                        foreach (var d in ordered) // priority order, first match wins
                            if (DeviceNameMatches(currentName, d.Name)) { checkedDev = d; break; }
                }

                var menu = new ContextMenu
                {
                    PlacementTarget = tb,
                    Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom,
                };
                int? lastType = null;
                foreach (var d in ordered)
                {
                    if (lastType != null && lastType != d.Type) menu.Items.Add(new Separator());
                    lastType = d.Type;
                    var captured = d;
                    var mi = new MenuItem
                    {
                        Header = $"{d.DriverLabel}: {d.DisplayName}",
                        IsChecked = checkedDev != null
                            && d.Type == checkedDev.Type && d.Name == checkedDev.Name,
                    };
                    mi.Click += (_, _) => ApplyDeviceSelection(isInput, hwIndex, captured);
                    menu.Items.Add(mi);
                }
                menu.IsOpen = true;
            });
        });
    }

    private void ApplyDeviceSelection(bool isInput, int hwIndex, VoicemeeterDevice dev)
    {
        Task.Run(() =>
        {
            bool ok = false;
            try { ok = VoicemeeterService.SetIoDevice(isInput, hwIndex, dev.Type, dev.Name); } catch { }
            Dispatcher.BeginInvoke(() =>
            {
                if (ok)
                {
                    string key = (isInput ? "S" : "B") + hwIndex;
                    // Show the picked device right away — the Voicemeeter readback
                    // lags the swap by up to ~2s, otherwise the name appears stuck.
                    _pendingDeviceWrites[(isInput, hwIndex)] = (dev.Name, DateTime.UtcNow);
                    // Remember which driver was picked so the menu checks exactly this
                    // one — in memory for this session and in settings for next launch.
                    _lastPickedVmDevice[key] = (dev.Type, dev.Name);
                    var settings = SettingsService.Load();
                    settings.VoicemeeterDevicePicks[key] = $"{dev.Type}|{dev.Name}";

                    // While locked, the manual pick must become the new locked target,
                    // otherwise EnforceVoicemeeterLock would immediately revert it.
                    if (settings.VoicemeeterMuteLocked)
                    {
                        var snap = isInput
                            ? settings.VoicemeeterStripDeviceSnapshot
                            : settings.VoicemeeterBusDeviceSnapshot;
                        if (hwIndex >= 0 && hwIndex < snap.Count)
                        {
                            snap[hwIndex] = dev.Name;
                            ResetDeviceRestoreState();
                        }
                    }
                    SettingsService.Save();
                }
                else
                {
                    ((App)Application.Current).ShowBalloon(
                        "Voicemeeter", $"切换设备失败：{dev.DriverLabel}: {dev.DisplayName}", true);
                }
                RefreshVoicemeeterStatus();
            });
        });
    }

    private void UpdateVoicemeeterLockButton()
    {
        bool locked = SettingsService.Load().VoicemeeterMuteLocked;
        VoicemeeterLockBtn.Content = locked ? "\U0001F512" : "\U0001F513";
        VoicemeeterLockBtn.Foreground = locked
            ? new SolidColorBrush(Color.FromRgb(0xD9, 0x77, 0x06))
            : new SolidColorBrush(Color.FromRgb(0x9C, 0xA3, 0xAF));
        VoicemeeterLockBtn.ToolTip = locked
            ? "Strip 静音和设备路由已锁定 — 点击解锁"
            : "锁定当前 Strip 静音和设备路由（外部修改会自动恢复）";
    }

    private void VoicemeeterLock_Click(object sender, RoutedEventArgs e)
    {
        var settings = SettingsService.Load();
        if (settings.VoicemeeterMuteLocked)
        {
            settings.VoicemeeterMuteLocked = false;
            settings.VoicemeeterStripMuteSnapshot = [];
            settings.VoicemeeterStripDeviceSnapshot = [];
            settings.VoicemeeterBusDeviceSnapshot = [];
        }
        else
        {
            // Snapshot from the most recent rendered state — fallback to a fresh fetch.
            var state = _lastVoicemeeterState;
            if (state == null)
            {
                try { state = VoicemeeterService.GetIoState(); } catch { }
            }
            if (state == null) return;
            settings.VoicemeeterMuteLocked = true;
            settings.VoicemeeterStripMuteSnapshot = state.Inputs.Select(s => s.Muted).ToList();
            settings.VoicemeeterStripDeviceSnapshot = state.Inputs.Select(s => s.DeviceName ?? "").ToList();
            settings.VoicemeeterBusDeviceSnapshot = state.Outputs.Select(s => s.DeviceName ?? "").ToList();
        }
        SettingsService.Save();
        ResetDeviceRestoreState();
        UpdateVoicemeeterLockButton();
    }

    private static void ApplyMuteVisual(Border badge, TextBlock devText, bool muted)
    {
        badge.Background = muted
            ? (Brush)new SolidColorBrush(Color.FromRgb(0xFE, 0xE2, 0xE2))
            : Brushes.Transparent;
        badge.BorderBrush = muted
            ? (Brush)new SolidColorBrush(Color.FromRgb(0xFC, 0xA5, 0xA5))
            : (Brush)new SolidColorBrush(Color.FromRgb(0xC4, 0xB5, 0xFD));
        badge.ToolTip = muted ? "点击取消静音" : "点击静音";
        if (badge.Child is TextBlock txt)
        {
            txt.FontWeight = muted ? FontWeights.SemiBold : FontWeights.Normal;
            txt.Foreground = muted
                ? (Brush)new SolidColorBrush(Color.FromRgb(0xB9, 0x1C, 0x1C))
                : (Brush)new SolidColorBrush(Color.FromRgb(0x9C, 0xA3, 0xAF));
        }
        if (muted)
        {
            devText.TextDecorations = TextDecorations.Strikethrough;
            devText.Opacity = 0.6;
        }
        else
        {
            devText.TextDecorations = null;
            devText.Opacity = 1.0;
        }
    }

    private void LoadPlaybackDevices()
    {
        PlaybackList.Items.Clear();
        var settings = SettingsService.Load();
        var hidden = new HashSet<string>(settings.HiddenDeviceIds, StringComparer.OrdinalIgnoreCase);
        int hiddenCount = 0;
        foreach (var device in AudioDeviceService.GetPlaybackDevices(settings.ShowDisabledDevices))
        {
            bool isHidden = hidden.Contains(device.Id);
            if (isHidden) hiddenCount++;
            if (isHidden && !settings.ShowHiddenDevices) continue;
            bool isSelected = device.Id == _selectedPlaybackId;
            var btn = CreateDeviceButton(device, isHidden, isSelected, isPlayback: true);
            PlaybackList.Items.Add(btn);
        }
        UpdateHiddenCount(PlaybackHiddenCount, hiddenCount);
    }

    private void LoadRecordingDevices()
    {
        RecordingList.Items.Clear();
        var settings = SettingsService.Load();
        var hidden = new HashSet<string>(settings.HiddenDeviceIds, StringComparer.OrdinalIgnoreCase);
        int hiddenCount = 0;
        foreach (var device in AudioDeviceService.GetRecordingDevices(settings.ShowDisabledDevices))
        {
            bool isHidden = hidden.Contains(device.Id);
            if (isHidden) hiddenCount++;
            if (isHidden && !settings.ShowHiddenDevices) continue;
            bool isSelected = device.Id == _selectedRecordingId;
            var btn = CreateDeviceButton(device, isHidden, isSelected, isPlayback: false);
            RecordingList.Items.Add(btn);
        }
        UpdateHiddenCount(RecordingHiddenCount, hiddenCount);
    }

    private static void UpdateHiddenCount(TextBlock block, int count)
    {
        if (count > 0)
        {
            block.Text = $"({count} \u5DF2\u9690\u85CF)";
            block.Visibility = Visibility.Visible;
        }
        else
        {
            block.Visibility = Visibility.Collapsed;
        }
    }

    private Button CreateDeviceButton(AudioDeviceInfo device, bool isHidden = false, bool isSelected = false, bool isPlayback = true)
    {
        // Blue highlight = user selection only. Default device uses plain style + checkmark.
        var style = isSelected
            ? (Style)FindResource("DefaultDeviceButton")
            : (Style)FindResource("DeviceButton");

        var settings = SettingsService.Load();
        settings.DeviceNicknames.TryGetValue(device.Id, out var nickname);
        string displayName = !string.IsNullOrEmpty(nickname) ? nickname : device.Name;

        // Root = DockPanel. Indicator (✓/●) docked to the right so it doesn't shift text.
        var root = new DockPanel { LastChildFill = true };

        var indicator = new TextBlock
        {
            FontWeight = FontWeights.SemiBold,
            FontSize = 14,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
            Width = 20,
            Margin = new Thickness(6, 0, 2, 0),
            TextAlignment = TextAlignment.Center,
        };
        if (device.IsDefault && !device.IsDisabled)
        {
            indicator.Text = "\u2714";
            indicator.Foreground = new SolidColorBrush(Color.FromRgb(0x10, 0x9E, 0x7A));
        }
        else if (isSelected && !device.IsDisabled)
        {
            indicator.Text = "\u25CF";
            indicator.Foreground = new SolidColorBrush(Color.FromRgb(0x3B, 0x82, 0xF6));
        }
        DockPanel.SetDock(indicator, Dock.Right);
        root.Children.Add(indicator);

        var panel = new StackPanel { Orientation = Orientation.Horizontal };

        if (device.Icon != null)
        {
            var img = new Image
            {
                Source = device.Icon,
                Width = 48,
                Height = 48,
                Margin = new Thickness(0, 0, 6, 0),
            };
            RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.HighQuality);
            panel.Children.Add(img);
        }

        var nameStack = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        var nameRow = new StackPanel { Orientation = Orientation.Horizontal };
        nameRow.Children.Add(new TextBlock { Text = displayName, VerticalAlignment = VerticalAlignment.Center });
        if (device.IsDisabled)
        {
            nameRow.Children.Add(new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(0xF3, 0xF4, 0xF6)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(0xE5, 0xE7, 0xEB)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(3),
                Padding = new Thickness(5, 1, 5, 1),
                Margin = new Thickness(8, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Child = new TextBlock
                {
                    Text = "\u5DF2\u7981\u7528",
                    FontSize = 10,
                    Foreground = new SolidColorBrush(Color.FromRgb(0x6B, 0x72, 0x80)),
                },
            });
        }
        nameStack.Children.Add(nameRow);
        if (!string.IsNullOrEmpty(nickname))
        {
            nameStack.Children.Add(new TextBlock
            {
                Text = device.Name,
                FontSize = 11,
                Foreground = new SolidColorBrush(Color.FromRgb(0x6B, 0x72, 0x80)),
            });
        }
        panel.Children.Add(nameStack);
        root.Children.Add(panel);

        double opacity = 1.0;
        if (device.IsDisabled) opacity = 0.55;
        else if (isHidden) opacity = 0.5;

        object content = root;
        bool wantsPeak = device.IsDefault && !device.IsDisabled;       // peak bar: default device only
        bool wantsVolume = isPlayback && !device.IsDisabled;           // volume row: every playback device
        if (wantsPeak || wantsVolume)
        {
            var stack = new StackPanel();
            stack.Children.Add(root);

            if (wantsPeak)
            {
                var fill = new Border
                {
                    Background = new SolidColorBrush(Color.FromRgb(0x10, 0x9E, 0x7A)),
                    CornerRadius = new CornerRadius(2),
                    HorizontalAlignment = HorizontalAlignment.Left,
                    Width = 0,
                };
                var track = new Border
                {
                    Background = new SolidColorBrush(Color.FromRgb(0xE5, 0xE7, 0xEB)),
                    CornerRadius = new CornerRadius(2),
                    Height = 4,
                    Margin = new Thickness(2, 6, 2, 0),
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    Child = fill,
                    ClipToBounds = true,
                };
                stack.Children.Add(track);

                if (isPlayback) { _peakPlaybackTrack = track; _peakPlaybackFill = fill; }
                else { _peakRecordingTrack = track; _peakRecordingFill = fill; }
            }

            if (wantsVolume)
                stack.Children.Add(BuildVolumeRow(device.Id));

            content = stack;
        }

        var btn = new Button
        {
            Content = content,
            Tag = device.Id,
            Style = style,
            Opacity = opacity,
            Cursor = device.IsDisabled ? System.Windows.Input.Cursors.Arrow : System.Windows.Input.Cursors.Hand,
        };
        btn.Click += DeviceButton_Click;

        bool anyLocked = ((App)Application.Current).IsAnyProfileLocked();

        var menu = new ContextMenu();
        var setDefaultItem = new MenuItem { Header = "设为默认设备", Tag = device.Id };
        setDefaultItem.Click += SetDefaultDevice_Click;
        setDefaultItem.IsEnabled = !device.IsDefault && !device.IsDisabled && !anyLocked;
        if (anyLocked) setDefaultItem.ToolTip = "已锁定 — 请先解锁配置";
        menu.Items.Add(setDefaultItem);
        var toggleEnableItem = new MenuItem
        {
            Header = device.IsDisabled ? "启用此设备" : "禁用此设备",
            Tag = device.Id,
            IsEnabled = !anyLocked,
            ToolTip = anyLocked ? "已锁定 — 请先解锁配置" : null,
        };
        toggleEnableItem.Click += ToggleEnableDevice_Click;
        menu.Items.Add(toggleEnableItem);
        if (isPlayback)
        {
            var testItem = new MenuItem
            {
                Header = "测试播放",
                Tag = device.Id,
                IsEnabled = !device.IsDisabled,
            };
            testItem.Click += TestPlayback_Click;
            menu.Items.Add(testItem);
        }
        menu.Items.Add(new Separator());
        var renameItem = new MenuItem { Header = "重命名…", Tag = device };
        renameItem.Click += RenameDevice_Click;
        menu.Items.Add(renameItem);
        var toggleItem = new MenuItem { Header = isHidden ? "显示此设备" : "隐藏此设备", Tag = device.Id };
        toggleItem.Click += ToggleHideDevice_Click;
        menu.Items.Add(toggleItem);
        btn.ContextMenu = menu;

        return btn;
    }

    private void TestPlayback_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem mi || mi.Tag is not string deviceId) return;
        try
        {
            TestPlaybackService.Play(deviceId);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"无法在此设备上播放测试音：{ex.Message}",
                "测试播放", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void RenameDevice_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem mi || mi.Tag is not AudioDeviceInfo device) return;
        var settings = SettingsService.Load();
        settings.DeviceNicknames.TryGetValue(device.Id, out var current);

        // Dedup scope = same flow only (playback vs recording can share names)
        var playback = AudioDeviceService.GetPlaybackDevices();
        var sameCategory = playback.Any(d => string.Equals(d.Id, device.Id, StringComparison.OrdinalIgnoreCase))
            ? playback
            : AudioDeviceService.GetRecordingDevices();

        string? proposed = current;
        while (true)
        {
            var dlg = new DeviceRenameDialog(device.Name, proposed) { Owner = this };
            if (dlg.ShowDialog() != true) return;

            if (string.IsNullOrEmpty(dlg.Nickname))
            {
                settings.DeviceNicknames.Remove(device.Id);
                SettingsService.Save();
                LoadDevices();
                return;
            }

            bool conflict = sameCategory.Any(d =>
            {
                if (string.Equals(d.Id, device.Id, StringComparison.OrdinalIgnoreCase)) return false;
                var displayed = settings.DeviceNicknames.TryGetValue(d.Id, out var nn) && !string.IsNullOrEmpty(nn)
                    ? nn : d.Name;
                return string.Equals(displayed, dlg.Nickname, StringComparison.OrdinalIgnoreCase);
            });

            if (conflict)
            {
                MessageBox.Show($"\u540C\u7C7B\u8BBE\u5907\u4E2D\u5DF2\u5B58\u5728\u540D\u79F0\u300C{dlg.Nickname}\u300D\uFF0C\u8BF7\u6362\u4E00\u4E2A\u540D\u79F0\u3002",
                    "\u63D0\u793A", MessageBoxButton.OK, MessageBoxImage.Warning);
                proposed = dlg.Nickname;
                continue;
            }

            settings.DeviceNicknames[device.Id] = dlg.Nickname;
            SettingsService.Save();
            LoadDevices();
            return;
        }
    }

    private void ToggleHideDevice_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem mi || mi.Tag is not string deviceId) return;
        var settings = SettingsService.Load();
        var hidden = new HashSet<string>(settings.HiddenDeviceIds, StringComparer.OrdinalIgnoreCase);
        if (hidden.Contains(deviceId)) hidden.Remove(deviceId);
        else hidden.Add(deviceId);
        settings.HiddenDeviceIds = hidden.ToList();
        SettingsService.Save();
        LoadDevices();
    }

    private void ShowHidden_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded) return;
        var settings = SettingsService.Load();
        settings.ShowHiddenDevices = ShowHiddenCheck.IsChecked == true;
        SettingsService.Save();
        LoadDevices();
    }

    private void ShowDisabled_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded) return;
        var settings = SettingsService.Load();
        settings.ShowDisabledDevices = ShowDisabledCheck.IsChecked == true;
        SettingsService.Save();
        LoadDevices();
    }

    private void DeviceButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not string deviceId) return;

        // Left-click = toggle selection (for profile creation). Right-click opens context menu for "set default".
        var showDisabled = SettingsService.Load().ShowDisabledDevices;
        var playback = AudioDeviceService.GetPlaybackDevices(showDisabled);
        var recording = AudioDeviceService.GetRecordingDevices(showDisabled);
        var dev = playback.Find(d => d.Id == deviceId) ?? recording.Find(d => d.Id == deviceId);
        if (dev == null || dev.IsDisabled) return;

        bool isPlayback = playback.Any(d => d.Id == deviceId);
        if (isPlayback)
            _selectedPlaybackId = _selectedPlaybackId == deviceId ? null : deviceId;
        else
            _selectedRecordingId = _selectedRecordingId == deviceId ? null : deviceId;

        LoadDevices();
    }

    private void SetDefaultDevice_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem mi || mi.Tag is not string deviceId) return;
        if (!((App)Application.Current).TryUserChangeDevice()) return;
        try
        {
            AudioDeviceService.SetDefaultDevice(deviceId);
            ((App)Application.Current).MarkOwnChange();
            LoadDevices();
            LoadProfiles();
            _miniWindow?.LoadProfiles();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"\u5207\u6362\u8BBE\u5907\u5931\u8D25\uFF1A\n{ex.Message}", "\u9519\u8BEF",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void ToggleEnableDevice_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem mi || mi.Tag is not string deviceId) return;
        if (!((App)Application.Current).TryUserChangeDevice()) return;
        var dev = AudioDeviceService.GetPlaybackDevices(true).Find(d => d.Id == deviceId)
               ?? AudioDeviceService.GetRecordingDevices(true).Find(d => d.Id == deviceId);
        if (dev == null) return;

        try
        {
            AudioDeviceService.SetDeviceEnabled(deviceId, dev.IsDisabled);
            ((App)Application.Current).MarkOwnChange();
            LoadDevices();
            LoadProfiles();
            _miniWindow?.LoadProfiles();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"\u64CD\u4F5C\u5931\u8D25\uFF1A\n{ex.Message}", "\u9519\u8BEF",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    // ── Profiles ─────────────────────────────────────────────

    private void LoadProfiles()
    {
        ProfileList.Items.Clear();

        var profiles = ProfileService.GetAll();
        ProfileEmptyHint.Visibility = profiles.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        var allPlayback = AudioDeviceService.GetPlaybackDevices();
        var allRecording = AudioDeviceService.GetRecordingDevices();
        var currentPlayback = allPlayback.Find(d => d.IsDefault);
        var currentRecording = allRecording.Find(d => d.IsDefault);
        var currentPlaybackComm = AudioDeviceService.GetCommunicationsDefault(NAudio.CoreAudioApi.DataFlow.Render);
        var currentRecordingComm = AudioDeviceService.GetCommunicationsDefault(NAudio.CoreAudioApi.DataFlow.Capture);

        var nicknames = SettingsService.Load().DeviceNicknames;
        string ResolveDeviceName(string? id, string? rawName)
        {
            var raw = rawName ?? "\u65E0";
            if (!string.IsNullOrEmpty(id)
                && nicknames.TryGetValue(id, out var nn)
                && !string.IsNullOrEmpty(nn))
                return $"{nn} ({raw})";
            return raw;
        }

        bool anyActive = false;

        foreach (var profile in profiles)
        {
            bool isActive = profile.PlaybackDeviceId == currentPlayback?.Id
                         && profile.RecordingDeviceId == currentRecording?.Id
                         && profile.PlaybackDeviceId == currentPlaybackComm.Id
                         && profile.RecordingDeviceId == currentRecordingComm.Id;
            if (isActive) anyActive = true;

            var lockedSettingsId = SettingsService.Load().LockedProfileId;
            bool isLocked = lockedSettingsId == profile.Id;
            bool blockedByLock = lockedSettingsId.HasValue && !isLocked;

            // Left side: name + device info
            var nameRow = new StackPanel { Orientation = Orientation.Horizontal };
            if (!string.IsNullOrEmpty(profile.Color))
            {
                try
                {
                    var color = (Color)ColorConverter.ConvertFromString(profile.Color);
                    nameRow.Children.Add(new System.Windows.Shapes.Ellipse
                    {
                        Width = 10,
                        Height = 10,
                        Fill = new SolidColorBrush(color),
                        Margin = new Thickness(0, 0, 6, 0),
                        VerticalAlignment = VerticalAlignment.Center,
                    });
                }
                catch { }
            }
            if (isLocked)
            {
                nameRow.Children.Add(new TextBlock
                {
                    Text = "🔒 ",
                    FontSize = 12,
                    Foreground = new SolidColorBrush(Color.FromRgb(0xD9, 0x77, 0x06)),
                    VerticalAlignment = VerticalAlignment.Center,
                });
            }
            var nameText = new TextBlock
            {
                Text = (isActive ? "\u2714 " : "") + profile.Name,
                FontSize = 13,
                FontWeight = FontWeights.SemiBold,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = isActive
                    ? new SolidColorBrush(Color.FromRgb(0x1E, 0x40, 0xAF))
                    : new SolidColorBrush(Color.FromRgb(0x11, 0x18, 0x27)),
            };
            nameRow.Children.Add(nameText);

            bool playbackMissing = !string.IsNullOrEmpty(profile.PlaybackDeviceId)
                && !allPlayback.Any(d => string.Equals(d.Id, profile.PlaybackDeviceId, StringComparison.Ordinal));
            bool recordingMissing = !string.IsNullOrEmpty(profile.RecordingDeviceId)
                && !allRecording.Any(d => string.Equals(d.Id, profile.RecordingDeviceId, StringComparison.Ordinal));
            var missingBrush = new SolidColorBrush(Color.FromRgb(0xD3, 0x2F, 0x2F));

            var detailText = new TextBlock
            {
                FontSize = 11,
                Foreground = new SolidColorBrush(Color.FromRgb(0x6B, 0x72, 0x80)),
                Margin = new Thickness(0, 3, 0, 0),
                TextTrimming = TextTrimming.CharacterEllipsis,
            };

            detailText.Inlines.Add(new Run($"▶ {ResolveDeviceName(profile.PlaybackDeviceId, profile.PlaybackDeviceName)}"));
            if (playbackMissing)
                detailText.Inlines.Add(new Run(" 未连接") { Foreground = missingBrush, FontWeight = FontWeights.SemiBold });
            detailText.Inlines.Add(new Run($"  ·  ● {ResolveDeviceName(profile.RecordingDeviceId, profile.RecordingDeviceName)}"));
            if (recordingMissing)
                detailText.Inlines.Add(new Run(" 未连接") { Foreground = missingBrush, FontWeight = FontWeights.SemiBold });
            if (profile.HotkeyKey != 0)
                detailText.Inlines.Add(new Run($"  ·  ⌨ {FormatHotkey(profile.HotkeyModifiers, profile.HotkeyKey)}"));

            var infoPanel = new StackPanel();
            infoPanel.Children.Add(nameRow);
            infoPanel.Children.Add(detailText);

            var applyBtn = new Button
            {
                Content = infoPanel,
                Style = (Style)FindResource("ProfileApplyButton"),
                Tag = profile.Id,
            };
            applyBtn.Click += ApplyProfile_Click;

            // Right side: lock + edit + delete links
            var lockBtn = new Button
            {
                Content = isLocked ? "\U0001F512" : "\U0001F513",
                Style = (Style)FindResource("SmallLinkButton"),
                Tag = profile.Id,
                Foreground = isLocked
                    ? new SolidColorBrush(Color.FromRgb(0xD9, 0x77, 0x06))
                    : new SolidColorBrush(Color.FromRgb(0x9C, 0xA3, 0xAF)),
                ToolTip = isLocked ? "已锁定 — 点击解锁" : "锁定到此配置",
                FontSize = 13,
            };
            lockBtn.Click += LockProfile_Click;

            var editBtn = new Button
            {
                Content = "\u7F16\u8F91",
                Style = (Style)FindResource("SmallLinkButton"),
                Tag = profile.Id,
            };
            editBtn.Click += EditProfile_Click;

            var separator = new TextBlock
            {
                Text = "|",
                Foreground = new SolidColorBrush(Color.FromRgb(0xD1, 0xD5, 0xDB)),
                VerticalAlignment = VerticalAlignment.Center,
                FontSize = 11,
            };

            var deleteBtn = new Button
            {
                Content = "\u5220\u9664",
                Style = (Style)FindResource("SmallLinkButton"),
                Tag = profile.Id,
                Foreground = new SolidColorBrush(Color.FromRgb(0xDC, 0x26, 0x26)),
            };
            deleteBtn.Click += DeleteProfile_Click;

            var actionPanel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                VerticalAlignment = VerticalAlignment.Center,
            };
            actionPanel.Children.Add(lockBtn);
            actionPanel.Children.Add(new TextBlock
            {
                Text = "|",
                Foreground = new SolidColorBrush(Color.FromRgb(0xD1, 0xD5, 0xDB)),
                VerticalAlignment = VerticalAlignment.Center,
                FontSize = 11,
                Margin = new Thickness(2, 0, 2, 0),
            });
            actionPanel.Children.Add(editBtn);
            actionPanel.Children.Add(separator);
            actionPanel.Children.Add(deleteBtn);

            // Drag handle (only this region initiates a drag; rest of card stays clickable to apply)
            var grip = new TextBlock
            {
                Text = "☰",
                FontSize = 14,
                Foreground = new SolidColorBrush(Color.FromRgb(0x9C, 0xA3, 0xAF)),
                Margin = new Thickness(0, 0, 8, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Cursor = Cursors.SizeAll,
                Tag = profile.Id,
                ToolTip = "拖拽调整顺序",
            };
            grip.PreviewMouseLeftButtonDown += ProfileGrip_MouseDown;
            grip.PreviewMouseMove += ProfileGrip_MouseMove;

            // Card layout
            var dock = new DockPanel();
            DockPanel.SetDock(grip, Dock.Left);
            dock.Children.Add(grip);
            DockPanel.SetDock(actionPanel, Dock.Right);
            dock.Children.Add(actionPanel);
            dock.Children.Add(applyBtn);

            var card = new Border
            {
                Style = (Style)FindResource(isActive ? "ActiveProfileCard" : "ProfileCard"),
                Child = dock,
                Cursor = blockedByLock ? System.Windows.Input.Cursors.Arrow : System.Windows.Input.Cursors.Hand,
                Tag = profile.Id,
                AllowDrop = true,
                Opacity = blockedByLock ? 0.5 : 1.0,
                ToolTip = blockedByLock ? "已被锁定到其他配置 — 先解锁才能切换" : null,
            };
            card.MouseLeftButtonUp += (_, e) =>
            {
                if (e.ChangedButton != System.Windows.Input.MouseButton.Left) return;
                ApplyProfile_Click(applyBtn, e);
            };
            card.PreviewDragOver += ProfileCard_DragOver;
            card.Drop += ProfileCard_Drop;

            ProfileList.Items.Add(card);
        }

        bool showWarning = profiles.Count > 0 && !anyActive;
        ProfileWarningText.Visibility = showWarning ? Visibility.Visible : Visibility.Collapsed;
        ProfileWarningText.Tag = (showWarning && SettingsService.Load().EnableBlinkAnimation) ? "Blink" : null;

        var drifted = ((App)Application.Current).DriftedApps;
        if (drifted.Count > 0)
        {
            AppDriftWarningText.Text = $"{drifted.Count} \u4E2A\u5E94\u7528\u504F\u79BB: {string.Join(", ", drifted)}";
            AppDriftWarningText.Visibility = Visibility.Visible;
        }
        else
        {
            AppDriftWarningText.Visibility = Visibility.Collapsed;
        }
    }

    private void ProfileGrip_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not TextBlock tb || tb.Tag is not Guid id) return;
        _profileDragStart = e.GetPosition(this);
        _profileDragSourceId = id;
    }

    private void ProfileGrip_MouseMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed) return;
        if (_profileDragSourceId == Guid.Empty) return;
        if (sender is not TextBlock tb) return;

        var pos = e.GetPosition(this);
        if (Math.Abs(pos.X - _profileDragStart.X) < SystemParameters.MinimumHorizontalDragDistance
         && Math.Abs(pos.Y - _profileDragStart.Y) < SystemParameters.MinimumVerticalDragDistance) return;

        try
        {
            DragDrop.DoDragDrop(tb, new DataObject("ProfileId", _profileDragSourceId), DragDropEffects.Move);
        }
        finally
        {
            _profileDragSourceId = Guid.Empty;
        }
    }

    private void ProfileCard_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent("ProfileId") ? DragDropEffects.Move : DragDropEffects.None;
        e.Handled = true;
    }

    private void ProfileCard_Drop(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent("ProfileId")) return;
        if (e.Data.GetData("ProfileId") is not Guid sourceId) return;
        if (sender is not Border target || target.Tag is not Guid targetId) return;
        if (sourceId == targetId) return;

        var profiles = ProfileService.GetAll();
        var src = profiles.Find(p => p.Id == sourceId);
        if (src == null) return;
        profiles.Remove(src);

        int targetIdx = profiles.FindIndex(p => p.Id == targetId);
        if (targetIdx < 0) { profiles.Add(src); }
        else
        {
            // Drop above or below depending on cursor position within the target card
            var pos = e.GetPosition(target);
            if (pos.Y > target.ActualHeight / 2) targetIdx++;
            profiles.Insert(targetIdx, src);
        }

        for (int i = 0; i < profiles.Count; i++) profiles[i].Order = i + 1;
        ProfileService.SaveAll(profiles);

        LoadProfiles();
        _miniWindow?.LoadProfiles();
    }

    private void ApplyProfile_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not Guid id) return;

        var profile = ProfileService.GetAll().Find(p => p.Id == id);
        if (profile == null) return;

        if (!((App)Application.Current).TryUserApplyProfile(profile)) return;

        try
        {
            var result = ProfileApplyService.Apply(profile);
            ((App)Application.Current).MarkOwnChange();
            LoadDevices();
            LoadProfiles();
            _miniWindow?.LoadProfiles();
            ((App)Application.Current).NotifyProfileApplied(profile, result);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"\u5E94\u7528\u914D\u7F6E\u5931\u8D25\uFF1A\n{ex.Message}", "\u9519\u8BEF",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void NewProfile_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new ProfileEditDialog { Owner = this };
        if (dialog.ShowDialog() != true) return;

        // Use user-selected devices if set, otherwise fall back to current defaults.
        var allPlayback = AudioDeviceService.GetPlaybackDevices();
        var allRecording = AudioDeviceService.GetRecordingDevices();
        var playback = _selectedPlaybackId != null
            ? allPlayback.Find(d => d.Id == _selectedPlaybackId)
            : allPlayback.Find(d => d.IsDefault);
        var recording = _selectedRecordingId != null
            ? allRecording.Find(d => d.Id == _selectedRecordingId)
            : allRecording.Find(d => d.IsDefault);

        var profile = new DeviceProfile
        {
            Name = dialog.ProfileName,
            PlaybackDeviceId = playback?.Id,
            PlaybackDeviceName = playback?.Name,
            RecordingDeviceId = recording?.Id,
            RecordingDeviceName = recording?.Name,
            HotkeyModifiers = dialog.HotkeyModifiers,
            HotkeyKey = dialog.HotkeyKey,
            AppOverrides = dialog.AppOverrides,
            RestartVoicemeeterAfterApply = dialog.RestartVoicemeeter,
            ShowInMiniWindow = dialog.ShowInMiniWindow,
            Color = dialog.SelectedColor,
        };
        ProfileService.Save(profile);
        RegisterProfileHotkeys();

        _selectedPlaybackId = null;
        _selectedRecordingId = null;

        LoadDevices();
        LoadProfiles();
        _miniWindow?.LoadProfiles();
    }

    private void EditProfile_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not Guid id) return;

        var profile = ProfileService.GetAll().Find(p => p.Id == id);
        if (profile == null) return;

        var dialog = new ProfileEditDialog(profile.Name, profile.HotkeyModifiers, profile.HotkeyKey, profile.AppOverrides, profile.RestartVoicemeeterAfterApply, profile.ShowInMiniWindow, profile.Color) { Owner = this };
        if (dialog.ShowDialog() != true) return;

        profile.Name = dialog.ProfileName;
        profile.HotkeyModifiers = dialog.HotkeyModifiers;
        profile.HotkeyKey = dialog.HotkeyKey;
        profile.AppOverrides = dialog.AppOverrides;
        profile.RestartVoicemeeterAfterApply = dialog.RestartVoicemeeter;
        profile.ShowInMiniWindow = dialog.ShowInMiniWindow;
        profile.Color = dialog.SelectedColor;
        ProfileService.Save(profile);
        RegisterProfileHotkeys();
        LoadProfiles();
        _miniWindow?.LoadProfiles();
    }

    private void DeleteProfile_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not Guid id) return;

        var profile = ProfileService.GetAll().Find(p => p.Id == id);
        if (profile == null) return;

        var result = MessageBox.Show($"\u786E\u5B9A\u5220\u9664\u914D\u7F6E \"{profile.Name}\"\uFF1F", "\u786E\u8BA4",
            MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (result == MessageBoxResult.Yes)
        {
            ProfileService.Delete(id);
            // Clear lock if the deleted profile was the locked one.
            var s = SettingsService.Load();
            if (s.LockedProfileId == id)
            {
                s.LockedProfileId = null;
                SettingsService.Save();
            }
            RegisterProfileHotkeys();
            LoadProfiles();
            _miniWindow?.LoadProfiles();
        }
    }

    private void LockProfile_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not Guid id) return;
        var settings = SettingsService.Load();
        bool wasLocked = settings.LockedProfileId == id;
        settings.LockedProfileId = wasLocked ? null : id;
        SettingsService.Save();

        if (!wasLocked)
        {
            // Locking implies "make the system match this profile right now".
            var profile = ProfileService.GetAll().Find(p => p.Id == id);
            if (profile != null)
            {
                try
                {
                    ProfileApplyService.Apply(profile);
                    ((App)Application.Current).MarkOwnChange();
                }
                catch { }
            }
        }

        LoadDevices();
        LoadProfiles();
        _miniWindow?.LoadProfiles();
    }

    // ── Other ────────────────────────────────────────────────

    private void Refresh_Click(object sender, RoutedEventArgs e)
    {
        LoadProfiles();
        LoadDevices();
    }

    private void OpenSoundSettings_Click(object sender, RoutedEventArgs e)
    {
        Process.Start(new ProcessStartInfo("rundll32.exe", "shell32.dll,Control_RunDLL mmsys.cpl,,0")
        {
            UseShellExecute = false
        });
    }

    private void OpenSettings_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new SettingsWindow { Owner = this };
        if (dlg.ShowDialog() == true)
        {
            _lastStateSignature = "";
            RefreshFromExternalChange();
            _miniWindow?.LoadProfiles();
        }
    }

    private void ExportConfig_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Title = "导出配置",
            Filter = "配置备份 (*.json)|*.json",
            FileName = $"AudioDeviceSwitcher-backup-{DateTime.Now:yyyyMMdd}.json",
            DefaultExt = ".json",
        };
        if (dlg.ShowDialog(this) != true) return;
        try
        {
            BackupService.Export(dlg.FileName);
            MessageBox.Show("配置已导出。", "导出配置", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"导出失败：\n{ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void ImportConfig_Click(object sender, RoutedEventArgs e)
    {
        if (((App)Application.Current).IsAnyProfileLocked())
        {
            MessageBox.Show("已锁定 — 请先解锁配置再导入。", "导入配置",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "导入配置",
            Filter = "配置备份 (*.json)|*.json|所有文件 (*.*)|*.*",
            CheckFileExists = true,
        };
        if (dlg.ShowDialog(this) != true) return;
        try
        {
            var (profiles, appProfiles, nicknames) = BackupService.ImportMerge(dlg.FileName);

            // Imported profiles may carry hotkeys; rebuild registrations and refresh both windows.
            RegisterProfileHotkeys();
            _lastStateSignature = "";
            RefreshFromExternalChange();
            LoadProfiles();
            _miniWindow?.LoadProfiles();

            MessageBox.Show(
                $"导入完成（合并）：\n配置 {profiles} 个、应用预设 {appProfiles} 个、设备别名 {nicknames} 个。",
                "导入配置", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"导入失败：\n{ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void OpenAbout_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new AboutWindow { Owner = this };
        dlg.ShowDialog();
    }

    private void OpenAppAudio_Click(object sender, RoutedEventArgs e)
    {
        var win = new AppAudioWindow { Owner = this };
        win.ShowDialog();
    }

    private void OpenAppProfiles_Click(object sender, RoutedEventArgs e)
    {
        var win = new AppProfilesWindow { Owner = this };
        win.ShowDialog();
    }

    private void ExitApp_Click(object sender, RoutedEventArgs e)
    {
        var result = MessageBox.Show("\u786E\u5B9A\u9000\u51FA\u97F3\u9891\u5207\u6362\u52A9\u624B\uFF1F", "\u786E\u8BA4",
            MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (result == MessageBoxResult.Yes)
        {
            ((App)Application.Current).ExitFromUI();
        }
    }

    private static string FormatHotkey(int modifiers, int key)
    {
        var parts = new List<string>();
        if ((modifiers & 0x0002) != 0) parts.Add("Ctrl");
        if ((modifiers & 0x0001) != 0) parts.Add("Alt");
        if ((modifiers & 0x0004) != 0) parts.Add("Shift");
        parts.Add(KeyInterop.KeyFromVirtualKey(key).ToString());
        return string.Join("+", parts);
    }
}
