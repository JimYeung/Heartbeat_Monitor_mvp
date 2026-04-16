namespace Monitor.Services;

/// <summary>
/// Processes raw ADC samples to extract heart rate in BPM.
///
/// Pipeline:
///   1. 2nd-order Butterworth bandpass IIR filter (5–15 Hz) — isolates QRS complex
///   2. Peak detector with 200 ms refractory period — finds R waves
///   3. BPM calculator — rolling average of last 5 RR intervals
/// </summary>
public sealed class SignalProcessor
{
    // -----------------------------------------------------------------------
    // Bandpass filter — 2nd-order Butterworth, 5–15 Hz @ 250 Hz sample rate.
    // Derived via bilinear transform (see docs/architecture.md).
    // Difference equation: y[n] = B0*(x[n] - x[n-2]) + A1*y[n-1] - A2*y[n-2]
    // -----------------------------------------------------------------------
    private const double B0 = 0.11362;   // b[0] = -b[2]; b[1] = 0
    private const double A1 = 1.73031;   // denominator coefficients (negated std form)
    private const double A2 = 0.77276;

    private double _x1, _x2;   // x[n-1], x[n-2]
    private double _y1, _y2;   // y[n-1], y[n-2]

    // -----------------------------------------------------------------------
    // Peak detection
    // -----------------------------------------------------------------------
    private const int RefractorySamples = 50;   // 200 ms @ 250 Hz — SR-06

    // Adaptive envelope tracks the largest filtered value seen.
    // Threshold = envelope * 0.5 — set high enough to ignore noise, low
    // enough to catch attenuated R-waves.
    private double _envelope = 300.0;
    private int _refractoryCountdown;
    private int _samplesSincePeak;
    private bool _firstPeakSeen;

    // -----------------------------------------------------------------------
    // BPM — rolling average of the last 5 RR intervals (in samples)
    // -----------------------------------------------------------------------
    private readonly int[] _rrBuffer = new int[5];
    private int _rrHead;
    private int _rrCount;

    public double CurrentBpm { get; private set; }

    /// <summary>
    /// Feed one raw ADC sample. Returns the bandpass-filtered value.
    /// <see cref="CurrentBpm"/> is updated whenever a new R-wave is detected.
    /// </summary>
    public double Feed(double rawSample)
    {
        // Step 1 — bandpass filter
        double filtered = B0 * rawSample - B0 * _x2 + A1 * _y1 - A2 * _y2;
        _x2 = _x1;  _x1 = rawSample;
        _y2 = _y1;  _y1 = filtered;

        // Step 2 — update adaptive envelope (rises instantly, decays slowly)
        double abs = Math.Abs(filtered);
        _envelope = abs > _envelope ? abs : _envelope * 0.9998;

        // Step 3 — peak detection
        _samplesSincePeak++;

        if (_refractoryCountdown > 0)
        {
            _refractoryCountdown--;
        }
        else if (filtered > _envelope * 0.5)
        {
            int rr = _samplesSincePeak;
            _samplesSincePeak = 0;
            _refractoryCountdown = RefractorySamples;

            if (_firstPeakSeen)
            {
                // Record RR interval and recompute BPM
                _rrBuffer[_rrHead] = rr;
                _rrHead = (_rrHead + 1) % _rrBuffer.Length;
                if (_rrCount < _rrBuffer.Length) _rrCount++;

                int sum = 0;
                for (int i = 0; i < _rrCount; i++) sum += _rrBuffer[i];
                CurrentBpm = 60.0 * 250.0 / (sum / (double)_rrCount);
            }

            _firstPeakSeen = true;
        }

        return filtered;
    }
}