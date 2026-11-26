using NISS.Application.Screenshots;

namespace NISS.Api.Tests;

public class ScreenshotDiskGuardTests
{
    [Fact]
    public void Blocks_capture_when_free_space_is_below_the_threshold()
    {
        Assert.False(ScreenshotDiskGuard.HasSpace(100L * 1024 * 1024, minFreeMb: 2048)); // 100 MB free, 2 GB required
    }

    [Fact]
    public void Allows_capture_when_free_space_is_at_or_above_the_threshold()
    {
        Assert.True(ScreenshotDiskGuard.HasSpace(5L * 1024 * 1024 * 1024, minFreeMb: 2048)); // 5 GB free
        Assert.True(ScreenshotDiskGuard.HasSpace(2048L * 1024 * 1024, minFreeMb: 2048)); // exactly at threshold
    }

    [Fact]
    public void Guard_disabled_when_threshold_is_zero()
    {
        Assert.True(ScreenshotDiskGuard.HasSpace(0, minFreeMb: 0));
    }
}
