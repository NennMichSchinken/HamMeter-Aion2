using HamMeter.UI;
using Xunit;

namespace HamMeter.Tests;

public class IconTests
{
    public static TheoryData<Icon> AllIcons()
    {
        var data = new TheoryData<Icon>();
        foreach (Icon i in Enum.GetValues<Icon>())
        {
            data.Add(i);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(AllIcons))]
    public void EveryIcon_IsEmbeddedAndParses(Icon icon)
    {
        LucideIcon? li = Icons.Get(icon);

        Assert.NotNull(li);
        Assert.True(li!.StrokeCount > 0);
        Assert.True(li.InsideViewBox);
    }
}
