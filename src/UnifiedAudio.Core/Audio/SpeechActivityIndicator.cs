namespace UnifiedAudio.Core.Audio;

public sealed class SpeechActivityIndicator
{
    private long _activeUntil;

    public bool Update(float outputPeak, float thresholdDb, bool muted, bool running, long milliseconds)
    {
        if (muted || !running) { _activeUntil = 0; return false; }
        if (float.IsFinite(outputPeak) && outputPeak > 0
            && 20 * Math.Log10(outputPeak) >= thresholdDb)
            _activeUntil = milliseconds + 500;
        return milliseconds < _activeUntil;
    }
}
