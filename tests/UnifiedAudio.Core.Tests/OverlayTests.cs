using UnifiedAudio.Core.Audio;

namespace UnifiedAudio.Core.Tests;

public sealed class OverlayTests
{
    [Theory]
    [InlineData(32)]
    [InlineData(64)]
    [InlineData(96)]
    [InlineData(192)]
    public void CircleIsCenteredAndHasNoMatteAtAnyScale(int size)
    {
        var pixels = OverlayCircle.Render(size, 255, 0, 0, 255, 50);
        for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                var offset = (y * size + x) * 4;
                Assert.Equal(pixels[offset + 3], pixels[(y * size + size - 1 - x) * 4 + 3]);
                Assert.Equal(pixels[offset + 3], pixels[((size - 1 - y) * size + x) * 4 + 3]);
                Assert.Equal(0, pixels[offset]);
                Assert.Equal(0, pixels[offset + 1]);
                Assert.Equal(pixels[offset + 3], pixels[offset + 2]);
            }
        Assert.Equal(0, pixels[3]);
        Assert.Equal(128, pixels[(size / 2 * size + size / 2) * 4 + 3]);
    }

    [Fact]
    public void OpacityScalesTheCircleAlphaWithoutAddingWhite()
    {
        var full = OverlayCircle.Render(64, 10, 200, 80, 255, 100);
        var faint = OverlayCircle.Render(64, 10, 200, 80, 255, 10);
        var center = (32 * 64 + 32) * 4;
        Assert.Equal(255, full[center + 3]);
        Assert.Equal(26, faint[center + 3]);
        Assert.InRange(faint[center + 1], (byte)19, (byte)21);
        Assert.All(OverlayCircle.Render(64, 255, 255, 255, 255, 0), b => Assert.Equal(0, b));
    }

    [Fact]
    public void SuppressedOutputStaysIdleAndActivityEndsWhenFiltersClose()
    {
        var activity = new SpeechActivityIndicator();
        // RNNoise/gates can produce silence even with a loud physical input.
        Assert.False(activity.Update(0, -40, false, true, 1000));
        Assert.False(activity.Update(0.001f, -40, false, true, 1100));
        Assert.True(activity.Update(0.02f, -40, false, true, 1200));
        Assert.False(activity.Update(0, -40, false, true, 1700));
        // Muting or stopping clears activity immediately, including its hold.
        Assert.False(activity.Update(0.5f, -40, true, true, 1800));
        Assert.False(activity.Update(0, -40, false, true, 1850));
    }

    [Fact]
    public void ActivityUsesThresholdAndHoldsThroughShortSpeechPauses()
    {
        var activity = new SpeechActivityIndicator();
        Assert.False(activity.Update(0.001f, -40, false, true, 1000));
        Assert.True(activity.Update(0.1f, -40, false, true, 1100));
        Assert.True(activity.Update(0, -40, false, true, 1400));
        Assert.False(activity.Update(0, -40, false, true, 1600));
        Assert.True(activity.Update(0.001f, -70, false, true, 1800));
        Assert.False(activity.Update(1, -70, true, true, 1900));
        Assert.False(activity.Update(0, -70, false, true, 2000));
        Assert.False(activity.Update(1, -70, false, false, 2100));
    }
}
