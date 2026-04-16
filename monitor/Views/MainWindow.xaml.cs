using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using Monitor.ViewModels;
using ScottPlot;

namespace Monitor.Views;

public partial class MainWindow : Window
{
    private readonly MonitorViewModel _vm;
    private readonly DispatcherTimer _renderTimer;
    private string _lastSnapshotDir = string.Empty;

    public MainWindow()
    {
        InitializeComponent();

        _vm = new MonitorViewModel();
        DataContext = _vm;

        ConfigurePlot();

        // 60 Hz render timer — drains the sample channel and refreshes the plot.
        _renderTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
        _renderTimer.Tick += OnRenderTick;
        _renderTimer.Start();
    }

    private void ConfigurePlot()
    {
        var plot = WpfPlot.Plot;

        // Dark theme to match the window.
        plot.FigureBackground.Color = Color.FromHex("#1A1A2E");
        plot.DataBackground.Color   = Color.FromHex("#0F3460");
        plot.Axes.Color(Color.FromHex("#8892A4"));
        plot.Grid.MajorLineColor = Color.FromHex("#FFFFFF").WithAlpha(0.05);

        // Raw signal — green.
        var raw = plot.Add.Signal(_vm.WaveformBuffer);
        raw.Color      = Color.FromHex("#00FF88");
        raw.LineWidth  = 1.5f;
        raw.LegendText = "Raw";

        // Filtered signal (bandpass 5–15 Hz) — red.
        var filtered = plot.Add.Signal(_vm.FilteredBuffer);
        filtered.Color      = Color.FromHex("#ff0000");
        filtered.LineWidth  = 1.5f;
        filtered.LegendText = "Filtered (5–15 Hz)";

        plot.ShowLegend();

        // Y axis: covers the full ECG swing.
        plot.Axes.SetLimitsY(-3000, 3000);
        plot.Axes.AutoScaleX();

        plot.XLabel("Samples  (5 s window → 1 250 samples)");
        plot.YLabel("ADC value");
        plot.Title("ECG Waveform — Live");

        WpfPlot.Refresh();
    }

    private void OnRenderTick(object? sender, EventArgs e)
    {
        if (_vm.TryConsumeSamples())
            WpfPlot.Refresh();
    }

    // Resets zoom/pan back to the default view without touching the data buffers.
    private void OnResetView(object sender, RoutedEventArgs e)
    {
        WpfPlot.Plot.Axes.SetLimitsY(-3000, 3000);
        WpfPlot.Plot.Axes.AutoScaleX();
        WpfPlot.Refresh();
    }

    // Shows the nearest sample value when the mouse hovers over the plot.
    private void OnPlotMouseMove(object sender, MouseEventArgs e)
    {
        var pos    = e.GetPosition(WpfPlot);
        var coords = WpfPlot.Plot.GetCoordinates((float)pos.X, (float)pos.Y);

        // coords.X is the sample index; clamp to valid buffer range.
        int idx = (int)Math.Round(coords.X);
        if (idx < 0 || idx >= MonitorViewModel.BufferSize)
        {
            HoverTooltip.Visibility = Visibility.Collapsed;
            return;
        }

        double rawVal      = _vm.WaveformBuffer[idx];
        double filteredVal = _vm.FilteredBuffer[idx];

        HoverText.Text = $"Sample:   {idx}\nRaw:      {rawVal:F0}\nFiltered: {filteredVal:F1}";
        HoverTooltip.Visibility = Visibility.Visible;
    }

    private void OnPlotMouseLeave(object sender, MouseEventArgs e)
    {
        HoverTooltip.Visibility = Visibility.Collapsed;
    }

    // Saves a JPEG of the current plot and a companion CSV with both waveforms.
    // Both files are written to the same folder (My Documents) with a shared timestamp stem.
    private void OnSnapshot(object sender, RoutedEventArgs e)
    {
        var now  = DateTime.Now;
        var stem = $"ecg_snapshot_{now:yyyyMMdd_HHmmss}";
        var dir  = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        var png  = Path.Combine(dir, stem + ".png");
        var csv  = Path.Combine(dir, stem + ".csv");

        // Plot image
        int w = Math.Max((int)WpfPlot.ActualWidth,  1024);
        int h = Math.Max((int)WpfPlot.ActualHeight, 768);
        WpfPlot.Plot.SavePng(png, w, h);

        // CSV
        using var writer = new StreamWriter(csv);
        writer.WriteLine($"# Snapshot datetime: {now:O}");
        writer.WriteLine($"# Sample rate: 250 Hz");
        writer.WriteLine($"# Units: ADC counts (int16, zero-centred)");
        writer.WriteLine($"# Buffer size: {MonitorViewModel.BufferSize} samples  (5 s window)");
        writer.WriteLine("#");
        writer.WriteLine("Index,Raw_ADC,Filtered_ADC");
        for (int i = 0; i < MonitorViewModel.BufferSize; i++)
            writer.WriteLine($"{i},{_vm.WaveformBuffer[i]:F0},{_vm.FilteredBuffer[i]:F2}");

        // Show clickable link in the DISPLAY panel.
        _lastSnapshotDir       = dir;
        SnapshotLinkText.Text  = stem;
        SnapshotNotification.Visibility = Visibility.Visible;
    }

    private void OnSnapshotLinkClick(object sender, RoutedEventArgs e)
    {
        Process.Start(new ProcessStartInfo("explorer.exe", _lastSnapshotDir) { UseShellExecute = true });
    }
}