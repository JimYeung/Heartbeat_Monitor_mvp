using System.IO;
using System.Threading.Channels;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Monitor.Models;
using Monitor.Services;

namespace Monitor.ViewModels;

/// <summary>
/// Main ViewModel. Owns the device connection lifecycle, the rolling
/// waveform buffer, and the signal processor that computes BPM.
///
/// Threading model:
///   - TcpDeviceService writes Samples from a background thread into a Channel.
///   - TryConsumeSamples() is called by the View's DispatcherTimer on the UI thread,
///     draining the channel, running the signal processor, and updating observables.
/// </summary>
public sealed partial class MonitorViewModel : ObservableObject
{
    // 5 seconds × 250 Hz = 1250 samples visible at once (SR-05).
    public const int BufferSize = 1250;

    public double[] WaveformBuffer  { get; } = new double[BufferSize];
    public double[] FilteredBuffer  { get; } = new double[BufferSize];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ConnectCommand))]
    [NotifyCanExecuteChangedFor(nameof(DisconnectCommand))]
    private bool _isConnected;

    [ObservableProperty] private string _statusText    = "Disconnected";
    [ObservableProperty] private string _bpmText       = "--";
    [ObservableProperty] private string _droppedText   = "Dropped: 0";
    [ObservableProperty] private string _host          = "localhost";
    [ObservableProperty] private int    _port          = 5000;
    [ObservableProperty] private bool   _useSimulator  = true;

    // Display panel
    [ObservableProperty] private bool _isDisplayRaw = true;
    [ObservableProperty] private bool _isDisplayFiltered = true;
    [ObservableProperty] private bool   _isPaused;     // Pause — freezes display; channel keeps draining so no backlog builds up
    [ObservableProperty] private string _pauseText = "Pause";

    // BPM alarm panel
    [ObservableProperty] private bool _isBpmAlarm;
    [ObservableProperty] private int  _bpmMin = 50;
    [ObservableProperty] private int  _bpmMax = 120;

    // Recording
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ToggleRecordCommand))]
    private bool _isRecording;
    [ObservableProperty] private string _recordText = "Record";

    private readonly List<Sample> _recordBuffer = [];

    private IDeviceService?    _device;
    private CancellationTokenSource? _cts;
    private readonly SignalProcessor _processor = new();

    // -----------------------------------------------------------------------
    // Connect
    // -----------------------------------------------------------------------

    [RelayCommand(CanExecute = nameof(CanConnect))]
    private async Task ConnectAsync()
    {
        _cts = new CancellationTokenSource();

        IDeviceService service = UseSimulator
            ? new SimulatorDeviceService()
            : new TcpDeviceService(Host, Port);

        try
        {
            StatusText = UseSimulator
                ? "Connecting to simulator..."
                : $"Connecting to {Host}:{Port}...";

            await service.ConnectAsync(_cts.Token);

            _device = service;
            IsConnected = true;
            StatusText = UseSimulator
                ? "Simulator  |  250 Hz"
                : $"Connected  —  {Host}:{Port}  |  250 Hz";
        }
        catch (Exception ex)
        {
            await service.DisposeAsync();
            StatusText = $"Connection failed: {ex.Message}";
        }
    }

    private bool CanConnect() => !IsConnected;

    // -----------------------------------------------------------------------
    // Disconnect
    // -----------------------------------------------------------------------

    [RelayCommand(CanExecute = nameof(CanDisconnect))]
    private void Disconnect()
    {
        _cts?.Cancel();
        _device?.Disconnect();
        _device = null;
        IsConnected = false;
        IsBpmAlarm  = false;
        BpmText     = "--";
        StatusText  = "Disconnected";

        if (IsRecording) StopRecording();
    }

    private bool CanDisconnect() => IsConnected;

    // -----------------------------------------------------------------------
    // Pause toggle — freezes the display; data keeps flowing through the
    // processor so BPM stays live and the channel never backs up
    // -----------------------------------------------------------------------

    [RelayCommand]
    private void TogglePause()
    {
        IsPaused  = !IsPaused;
        PauseText = IsPaused ? "Resume" : "Pause";
    }

    [RelayCommand(CanExecute = nameof(IsConnected))]
    private void ToggleRecord()
    {
        if (IsRecording)
            StopRecording();
        else
            StartRecording();
    }

    private void StartRecording()
    {
        _recordBuffer.Clear();
        IsRecording = true;
        RecordText  = "Stop";
    }

    private void StopRecording()
    {
        IsRecording = false;
        RecordText  = "Record";

        if (_recordBuffer.Count == 0) return;

        var path = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            $"ecg_{DateTime.Now:yyyyMMdd_HHmmss}.csv");

        using var writer = new StreamWriter(path);
        writer.WriteLine("Timestamp,Sequence,Value");
        foreach (var s in _recordBuffer)
            writer.WriteLine($"{s.Timestamp:O},{s.Sequence},{s.Value}");

        StatusText = $"Saved → {path}";
    }

    // -----------------------------------------------------------------------
    // Buffer drain — called by the View's 60 Hz DispatcherTimer on the UI thread
    // -----------------------------------------------------------------------

    /// <summary>
    /// Drains all available samples from the channel, runs the signal processor,
    /// and updates WaveformBuffer + observable properties.
    /// Returns true if any samples were added (caller should refresh the plot).
    /// </summary>
    public bool TryConsumeSamples()
    {
        if (_device is null) return false;

        bool any = false;
        while (_device.Samples.TryRead(out Sample? sample))
        {
            // ScottPlot's AddSignal() takes data array instead of pointer
            // In terms of updating the Array, Array.Copy() is way more efficient than shifting in a for loop, although takes more memory
            // Always run the processor — maintains filter state and BPM even while paused.
            double fv = _processor.Feed(sample.Value);

            // Only update display buffers when not paused.
            if (!IsPaused)
            {
                Array.Copy(WaveformBuffer, 1, WaveformBuffer, 0, BufferSize - 1);
                WaveformBuffer[^1] = IsDisplayRaw ? sample.Value : 0;

                Array.Copy(FilteredBuffer, 1, FilteredBuffer, 0, BufferSize - 1);
                FilteredBuffer[^1] = IsDisplayFiltered ? fv : 0;
            }

            if (IsRecording)
                _recordBuffer.Add(sample);

            any = true;
        }

        if (any)
        {
            // Update BPM display (SR-06)
            double bpm = _processor.CurrentBpm;
            BpmText = bpm > 0 ? $"{bpm:F0}" : "--";

            // BPM alarm (SR-07)
            IsBpmAlarm = bpm > 0 && (bpm < BpmMin || bpm > BpmMax);

            // Dropped packet counter (SR-08)
            DroppedText = $"Dropped: {_device.DroppedPackets}";
        }

        return any;
    }
}