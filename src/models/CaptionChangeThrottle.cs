namespace LiveCaptionsTranslator.models;

/// <summary>Samples in-progress caption revisions while always allowing final sentences through.</summary>
public sealed class CaptionChangeThrottle
{
    private long currentSegmentId = -1;
    private int changeCount;

    public bool ShouldAttempt(long segmentId, int interval)
    {
        if (segmentId != currentSegmentId)
        {
            currentSegmentId = segmentId;
            changeCount = 0;
        }

        changeCount++;
        int safeInterval = Math.Clamp(interval, 1, 10);
        return changeCount % safeInterval == 0;
    }
}
