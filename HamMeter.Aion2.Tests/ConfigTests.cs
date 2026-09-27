using System.Numerics;
using Xunit;

namespace HamMeter.Tests;

public class ConfigTests
{
    [Fact]
    public void Migration_MovesUntouchedClassColours_AndKeepsTheUsersOwn()
    {
        Vector4 mine = new(0.1f, 0.2f, 0.3f, 1f);
        var config = new Config
        {
            Version = 1,
            CombatTimeout = 10f,
            JobColors = new Dictionary<string, Vector4>
            {
                ["ASN"] = ClassInfo.DefaultColorsV2()["ASN"], // old default: follows the icon now
                ["TEM"] = mine,                               // changed by the user: stays
            },
        };

        config.Migrate();

        Assert.Equal(ClassInfo.DefaultColors()["ASN"], config.JobColors["ASN"]);
        Assert.Equal(mine, config.JobColors["TEM"]);
        Assert.Equal(30f, config.CombatTimeout);
        Assert.Equal(Config.CurrentVersion, config.Version);
    }

    [Fact]
    public void EveryClass_HasAColourAndAnIcon()
    {
        foreach (string job in ClassInfo.Names.Keys)
        {
            Assert.True(ClassInfo.DefaultColors().ContainsKey(job), job);
            Assert.NotNull(typeof(ClassInfo).Assembly.GetManifestResourceStream(ClassInfo.IconResource(job)!));
        }
    }
}
