using HamMeter.UI;
using Xunit;

namespace HamMeter.Tests;

public class PopupPlacementTests
{
    private const float ListH = 300f;
    private const float Gap = 4f;

    private static float Top(float fieldTop, float monitorTop, float monitorBottom) =>
        PopupPlacement.ListTop(fieldTop, fieldTop + 30f, ListH, monitorTop, monitorBottom);

    [Fact]
    public void EnoughRoomBelow_OpensDownward()
    {
        Assert.Equal(200f + 30f + Gap, Top(200f, 0f, 1440f));
    }

    [Fact]
    public void NearBottomEdge_OpensUpward()
    {
        // Field at 1300 on a 1440-high screen: 106 px below, 1296 px above.
        Assert.Equal(1300f - Gap - ListH, Top(1300f, 0f, 1440f));
    }

    [Fact]
    public void TightBothWays_TakesTheRoomierSide()
    {
        // 250 px monitor slice: 116 px above, 100 px below -> up; flipped -> down.
        Assert.Equal(120f - Gap - ListH, Top(120f, 0f, 250f));
        Assert.Equal(100f + 30f + Gap, Top(100f, 0f, 250f));
    }

    [Fact]
    public void UsesTheMonitorTheFieldIsOn_NotTheOverlayOrigin()
    {
        // A monitor starting further down (e.g. offset second screen): same rule, own bounds.
        Assert.Equal(1800f - Gap - ListH, Top(1800f, 500f, 1900f));
    }
}
