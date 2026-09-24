using HamMeter.UI;
using Xunit;

namespace HamMeter.Tests;

public class BarStyleTests
{
    [Fact]
    public void NineStyles_LikeWispUI()
    {
        Assert.Equal(
            new[] { "Flat", "Smooth", "Gradient", "Bevel", "Sheen", "Glow", "Glow top", "Glow bottom", "Aurora" },
            BarStyles.All.Select(s => s.Name));
    }

    [Fact]
    public void EveryTexture_IsEmbedded()
    {
        var files = BarStyles.All.SelectMany(s => new[] { s.Texture, s.Light }).OfType<string>().Distinct();
        foreach (string file in files)
        {
            using Stream? s = typeof(BarStyles).Assembly.GetManifestResourceStream("HamMeter.Bars." + file);
            Assert.True(s is not null, $"missing embedded texture {file}");
        }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Removed in an update")]
    public void UnknownName_FallsBackToDefault(string? name)
    {
        Assert.Equal(BarStyles.DefaultName, BarStyles.Get(name).Name);
    }
}
