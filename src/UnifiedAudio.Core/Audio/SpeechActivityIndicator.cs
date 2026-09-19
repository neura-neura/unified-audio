namespace UnifiedAudio.Core.Audio;

public sealed class SpeechActivityIndicator
{
    private long _activeUntil;

    public bool Update(float inputPeak, float thresholdDb, bool muted, bool running, long milliseconds)
    {
        if (muted || !running) { _activeUntil = 0; return false; }
        if (float.IsFinite(inputPeak) && inputPeak > 0
            && 20 * Math.Log10(inputPeak) >= thresholdDb)
            _activeUntil = milliseconds + 500;
        return milliseconds < _activeUntil;
    }
}
