using LiveCaptionsTranslator.apis;
using LiveCaptionsTranslator.utils;

namespace LiveCaptionsTranslator.Tests;

public class LiveCaptionsHandlerTests
{
    [Fact]
    public void ProcessIdentityCheck_RejectsAReusedProcessIdWithDifferentStartTime()
    {
        DateTime originalStartTimeUtc = new(2026, 9, 27, 1, 2, 3, DateTimeKind.Utc);

        Assert.True(LiveCaptionsHandler.IsSameProcessInstance(
            originalStartTimeUtc, originalStartTimeUtc.ToLocalTime()));
        Assert.False(LiveCaptionsHandler.IsSameProcessInstance(
            originalStartTimeUtc, originalStartTimeUtc.AddMilliseconds(1)));
    }

    [Fact]
    public void ReleaseSession_KillsOwnedProcessByStoredIdWithoutReadingUnavailableWindow()
    {
        int killedProcessId = 0;
        DateTime killedProcessStartTime = default;
        bool restoredWindow = false;
        var processStartTimeUtc = new DateTime(2026, 9, 27, 1, 2, 3, DateTimeKind.Utc);
        var session = new LiveCaptionsSession(null!, 417, processStartTimeUtc,
            OwnsProcess: true, WasHidden: false);

        bool released = LiveCaptionsHandler.ReleaseSession(session,
            ownedSession =>
            {
                killedProcessId = ownedSession.ProcessId;
                killedProcessStartTime = ownedSession.ProcessStartTimeUtc;
                return true;
            },
            _ => restoredWindow = true);

        Assert.True(released);
        Assert.Equal(417, killedProcessId);
        Assert.Equal(processStartTimeUtc, killedProcessStartTime);
        Assert.False(restoredWindow);
    }

    [Fact]
    public void ReleaseSession_RestoresVisibleUserOwnedWindowWithoutKillingIt()
    {
        bool killedProcess = false;
        bool restoredWindow = false;
        var session = new LiveCaptionsSession(null!, 418, DateTime.UnixEpoch,
            OwnsProcess: false, WasHidden: false);

        bool released = LiveCaptionsHandler.ReleaseSession(session,
            _ =>
            {
                killedProcess = true;
                return true;
            },
            _ => restoredWindow = true);

        Assert.True(released);
        Assert.True(restoredWindow);
        Assert.False(killedProcess);
    }

    [Fact]
    public void ReleaseSession_LeavesInitiallyHiddenUserOwnedWindowAlone()
    {
        bool killedProcess = false;
        bool restoredWindow = false;
        var session = new LiveCaptionsSession(null!, 419, DateTime.UnixEpoch,
            OwnsProcess: false, WasHidden: true);

        bool released = LiveCaptionsHandler.ReleaseSession(session,
            _ =>
            {
                killedProcess = true;
                return true;
            },
            _ => restoredWindow = true);

        Assert.True(released);
        Assert.False(killedProcess);
        Assert.False(restoredWindow);
    }

    [Fact]
    public void InitializeSessionOrRelease_ReleasesSessionAndPreservesInitializationFailure()
    {
        var session = new LiveCaptionsSession(null!, 420, DateTime.UnixEpoch,
            OwnsProcess: true, WasHidden: false);
        var initializationError = new InvalidOperationException("Window initialization failed.");
        LiveCaptionsSession? releasedSession = null;

        InvalidOperationException thrown = Assert.Throws<InvalidOperationException>(() =>
            LiveCaptionsHandler.InitializeSessionOrRelease(
                session,
                _ => throw initializationError,
                released => releasedSession = released));

        Assert.Same(initializationError, thrown);
        Assert.Same(session, releasedSession);
    }

    [Fact]
    public void InitializeSessionOrRelease_CleanupFailureDoesNotMaskInitializationFailure()
    {
        var session = new LiveCaptionsSession(null!, 421, DateTime.UnixEpoch,
            OwnsProcess: true, WasHidden: false);
        var initializationError = new InvalidOperationException("Window initialization failed.");

        InvalidOperationException thrown = Assert.Throws<InvalidOperationException>(() =>
            LiveCaptionsHandler.InitializeSessionOrRelease(
                session,
                _ => throw initializationError,
                _ => throw new InvalidOperationException("Cleanup failed.")));

        Assert.Same(initializationError, thrown);
    }

    [Fact]
    public void CaptionWindowWithNegativeCoordinates_IsKeptWhenVisibleOnVirtualDesktop()
    {
        var virtualDesktop = new RECT { Left = -1920, Top = 0, Right = 1920, Bottom = 1080 };
        var captionWindow = new RECT { Left = -1600, Top = 120, Right = -800, Bottom = 360 };

        Assert.False(LiveCaptionsHandler.NeedsCaptionWindowCorrection(captionWindow, virtualDesktop));
    }

    [Theory]
    [InlineData(3000, 120, 3800, 360)]
    [InlineData(-1980, 120, -1880, 180)]
    public void CaptionWindowOffscreenOrWithOnlyASmallVisibleSliver_IsCorrected(
        int left, int top, int right, int bottom)
    {
        var virtualDesktop = new RECT { Left = -1920, Top = 0, Right = 1920, Bottom = 1080 };
        var captionWindow = new RECT { Left = left, Top = top, Right = right, Bottom = bottom };

        Assert.True(LiveCaptionsHandler.NeedsCaptionWindowCorrection(captionWindow, virtualDesktop));
    }
}
