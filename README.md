# Heartbeat Monitor

## Introduction

This project demonstrates a heartbeat monitor UI using a simulated ECG signal source. The signal source is either firmware running on an STM32 Nucleo-F767ZI board, or an internal software simulator built into the application for use when the dev board is not available. The desktop application is a WPF app running on Windows. This application includes codes generated from agentic tools. 

---

## Scope

Real-time acquisition, transmission, display, and analysis of a heartbeat signal for demonstration purposes.

### Design Goals

1. **Deterministic packet architecture** — each sample is transmitted as a fixed 8-byte packet with CRC-16 bit-error detection and observable dropped-packet counting.
2. **Lightweight application** — no dynamic allocation in the signal path; rolling display buffers are pre-allocated at startup.
3. **Minimal graphical tools** — pan, zoom, reset view, snapshot (JPG + CSV export), pause, and continuous CSV recording.

---

## Assumptions

- Signal sources transmit samples in 8-byte packets. 
- Sample rate is 250 Hz.
- ADC values are zero-centred int16 (range approximately −2000 to +2000).

---

## Architecture

### Overall

```
┌─────────────────────────────┐        ┌──────────────────────────────────┐
│   Signal Source             │        │   WPF Monitor Application        │
│                             │        │                                  │
│  STM32 Nucleo-F767ZI        │──USB──>│  TcpDeviceService                │
│  (USART3 @ 115200 baud)     │  VCP   │                                  │
│                             │        │───────────────OR─────────────────│
│────────────OR───────────────│        │                                  │
│                             │        │  SimulatorDeviceService          │
│  Software Simulator         │  ───>  │  (internal, no hardware needed)  │
└─────────────────────────────┘        │                                  │
                                       │  Channel<Sample>  (thread-safe)  │
                                       │         │                        │
                                       │         ▼  (60 Hz UI timer)      │
                                       │  SignalProcessor                 │
                                       │  (bandpass filter + BPM cal.)    │
                                       │         │                        │
                                       │         ▼                        │
                                       │  WaveformBuffer / FilteredBuffer │
                                       │  (ScottPlot live signal display) │
                                       └──────────────────────────────────┘
```

### UI Layout
Example UI Layout
<img src="docs/images/screenshot.png" width="800"/>

---

## Data Structure

### Packet Format (8 bytes)

| Byte | Field       | Type     | Description                              |
|------|-------------|----------|------------------------------------------|
| 0    | SOF1        | `uint8`  | `0xAA` — start of frame byte 1           |
| 1    | SOF2        | `uint8`  | `0x55` — start of frame byte 2           |
| 2–3  | Sequence    | `uint16` | Little-endian packet counter (wraps at 65535) |
| 4–5  | Value       | `int16`  | Little-endian ADC sample, zero-centred   |
| 6–7  | CRC*        | `uint16` | CRC-16 CCITT over bytes 2–5             |
*Cyclic Redundancy Check

### Sample Rate & Buffer

| Parameter     | Value                                      |
|---------------|--------------------------------------------|
| Sample rate   | 250 Hz                                     |
| Packet rate   | 250 packets/s                              |
| Baud rate     | 115200 (8N1)                               |
| Display window| 5 seconds = 1250 samples                   |
| Buffer type   | Fixed-size pre-allocated `double[]`        |

---

## Signal Processing

A 2nd-order Butterworth bandpass IIR filter (5–15 Hz @ 250 Hz sample rate) removes baseline drift and high-frequency noise. The filtered signal is used for R-peak detection and BPM calculation.

BPM is computed as a rolling average over the last 5 RR intervals:

```
BPM = 60 × 250 / mean(RR_samples)
```

See [docs/signal_processing.md](docs/signal_processing.md) for the full derivation.

---

## Project Structure

```
heartbeat_monitor/
├── monitor/                  WPF desktop application (.NET 8)
│   ├── Models/               Sample record
│   ├── Services/             IDeviceService, TcpDeviceService,
│   │                         SimulatorDeviceService, SignalProcessor, PacketParser
│   ├── ViewModels/           MonitorViewModel (MVVM)
│   └── Views/                MainWindow.xaml + code-behind
├── stm/                      STM32CubeIDE firmware project
│   └── heartbeat_fw/
│       └── Src/main.c        ECG table, TIM2 ISR, packet builder
├── docs/
│   ├── signal_processing.md  Filter derivation and BPM maths
│   └── sample_data/          MIT-BIH Arrhythmia Database (validation)
├── context/
│   └── PROTOCOL.md           Packet protocol specification
└── PROTOCOL.md               (root copy)
```
---
## Technical Deicision

### Fixed-size pre-allocated buffers
Rolling display buffers (`double[1250]`) are allocated once at startup and mutated
in-place using `Array.Copy`. This avoids GC pressure on the UI thread at 250 Hz and
keeps render latency predictable.

### Channel<T> as the thread boundary
`System.Threading.Channels.Channel<Sample>` acts as an interface between the producer 
(TCP/simulator) and the consumer (UI thread). The data is produced and consumed at the
same rate (250Hz) without the need of locking. 

### Polling render loop over data-change notifications
Instead of rendering the entire plot 250Hz, which would be expensive, the plot is rendered 
at a slower rate (60Hz), i.e. re-render the plot every ~4 samples, which is the standard 
refresh rate for human eyes. Hence,`DispatcherTimer` is used to govern the refreshing rate
per tick instead of using `INotifyPropertyChanged` (MVVP) which refresh on every sample. 

---

## Getting Started

### Run with the built-in simulator (no hardware needed)

1. Download `Monitor.exe` from the [Releases](../../releases) page.
2. Double-click to run — no installer or .NET runtime required.
3. Ensure **Simulator** is ticked in the CONNECTIVITY panel.
4. Click **Connect**. The ECG waveform starts immediately.

### Run with the Nucleo board

1. Flash `stm/heartbeat_fw` to the STM32 Nucleo-F767ZI via STM32CubeIDE.
2. Connect the board via CN1 (USB Mini-B, ST-Link port).
3. A Virtual COM Port will appear — use a serial-to-TCP bridge to forward it to `localhost:5000`.
4. Untick **Simulator**, set Host/Port, and click **Connect**.

### Build from source

```
dotnet build monitor/
dotnet run --project monitor/
```

---

## License

For demonstration purposes only. Not for clinical or diagnostic use.
