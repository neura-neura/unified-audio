namespace UnifiedAudio.Core.Audio;

/// <summary>
/// Reference implementation used by tests and the native engine specification.
/// The settled muted state always writes exact zeros.
/// </summary>
public sealed class MuteGate
{
    private float _gain = 1f;
    private bool _targetMuted;

    public MuteGate(int rampSamples = 64)
    {
        if (rampSamples <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(rampSamples));
        }

        RampSamples = rampSamples;
    }

    public int RampSamples { get; }
    public bool IsMuted => _targetMuted && _gain == 0f;

    public void SetMuted(bool muted) => _targetMuted = muted;

    public void Process(Span<float> interleavedSamples)
    {
        var target = _targetMuted ? 0f : 1f;
        if (_gain == target)
        {
            if (target == 0f)
            {
                interleavedSamples.Clear();
            }
            return;
        }

        var delta = (_targetMuted ? -1f : 1f) / RampSamples;
        for (var index = 0; index < interleavedSamples.Length; index++)
        {
            _gain = Math.Clamp(_gain + delta, 0f, 1f);
            interleavedSamples[index] *= _gain;
            if (_gain == target)
            {
                if (target == 0f && index + 1 < interleavedSamples.Length)
                {
                    interleavedSamples[(index + 1)..].Clear();
                }
                break;
            }
        }
    }
}
