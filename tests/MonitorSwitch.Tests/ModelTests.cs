using Xunit;
using MonitorSwitch.Models;
using MonitorSwitch.Services;

namespace MonitorSwitch.Tests;

public class ProfileStoreSalvageTests
{
    private static MonitorSnapshot Snap(string path = @"\\?\DISPLAY#TEST#1") => new()
    {
        TargetDevicePath = path,
        SourceWidth = 1920,
        SourceHeight = 1080,
    };

    [Fact]
    public void SalvageReadsPlainArray()
    {
        var json = """
            [
              { "id": "a", "name": "Work", "monitors": [ { "targetDevicePath": "\\\\?\\DISPLAY#A", "sourceWidth": 1920, "sourceHeight": 1080 } ] },
              { "id": "b", "name": "Home", "monitors": [ { "targetDevicePath": "\\\\?\\DISPLAY#B", "sourceWidth": 2560, "sourceHeight": 1440 } ] }
            ]
            """;
        var profiles = ProfileStore.Salvage(json);
        Assert.Equal(2, profiles.Count);
        Assert.Equal("Work", profiles[0].Name);
    }

    [Fact]
    public void SalvageReadsSingleObject()
    {
        var json = """{ "id": "a", "name": "Only", "monitors": [ { "targetDevicePath": "p", "sourceWidth": 1, "sourceHeight": 1 } ] }""";
        var profiles = ProfileStore.Salvage(json);
        Assert.Single(profiles);
        Assert.Equal("Only", profiles[0].Name);
    }

    [Fact]
    public void SalvageExtractsValidEntriesFromGarbage()
    {
        var json = """
            这是一段损坏的前缀文本 "broken"（无结构花括号）
            { "id": "good", "name": "Good", "monitors": [ { "targetDevicePath": "p", "sourceWidth": 1, "sourceHeight": 1 } ] }
            中间夹一段 "{ 字符串里的花括号不算结构 }"
            { "id": "empty", "name": "Empty", "monitors": [] }
            """;
        var profiles = ProfileStore.Salvage(json);
        Assert.Single(profiles); // 只有含显示器数据的那份被救回
        Assert.Equal("Good", profiles[0].Name);
    }

    [Fact]
    public void SalvageRecoversAfterStrayClosingBrace()
    {
        // 孤立 '}' 会让计数变负：必须归零重置，否则毒化后面真实对象的配对
        var json = """
            }}}
            { "id": "good", "name": "Good", "monitors": [ { "targetDevicePath": "p", "sourceWidth": 1, "sourceHeight": 1 } ] }
            """;
        var profiles = ProfileStore.Salvage(json);
        Assert.Single(profiles);
        Assert.Equal("Good", profiles[0].Name);
    }

    [Fact]
    public void SalvageReturnsEmptyForNothingUsable()
    {
        Assert.Empty(ProfileStore.Salvage("完全不是 JSON"));
        Assert.Empty(ProfileStore.Salvage(""));
    }

    [Fact]
    public void ExtensionDataSurvivesRoundTrip()
    {
        // 未知字段（更高版本写入）在解析 + 重新序列化后必须原样保留，防止降级丢数据
        var json = """{ "id": "a", "name": "N", "monitors": [ { "targetDevicePath": "p", "sourceWidth": 1, "sourceHeight": 1, "futureField": {"x": 1} } ], "futureTop": 42 }""";
        var profiles = ProfileStore.Salvage(json);
        var p = Assert.Single(profiles);
        Assert.True(p.ExtensionData?.ContainsKey("futureTop"));
        Assert.True(p.Monitors[0].ExtensionData?.ContainsKey("futureField"));

        var roundTrip = System.Text.Json.JsonSerializer.Serialize(p, ProfileStore.JsonOptions);
        Assert.Contains("futureTop", roundTrip);
        Assert.Contains("futureField", roundTrip);
    }
}

public class DisplayModelTests
{
    [Theory]
    [InlineData(1u, 2560u, 1600u, 2560u, 1600u)]
    [InlineData(2u, 2560u, 1600u, 1600u, 2560u)]
    [InlineData(3u, 2560u, 1600u, 2560u, 1600u)]
    [InlineData(4u, 2560u, 1600u, 1600u, 2560u)]
    public void DesktopDimensionsFollowRotation(uint rotation, uint sw, uint sh, uint dw, uint dh)
    {
        var m = new MonitorSnapshot { Rotation = rotation, SourceWidth = sw, SourceHeight = sh };
        Assert.Equal((dw, dh), (m.DesktopWidth, m.DesktopHeight));
    }

    [Fact]
    public void ProfileCloneIsDeep()
    {
        var p = new DisplayProfile
        {
            Name = "a",
            Hotkey = "Ctrl+1",
            Monitors = { Snap() },
        };
        var clone = p.Clone();
        clone.Name = "b";
        clone.Hotkey = null;
        clone.Monitors[0].SourceWidth = 999;

        Assert.Equal("a", p.Name);
        Assert.Equal("Ctrl+1", p.Hotkey);
        Assert.NotEqual(999u, p.Monitors[0].SourceWidth);
    }

    [Theory]
    [InlineData(148.0, "148 Hz")]
    [InlineData(148.5, "148.5 Hz")]
    [InlineData(59.94, "59.94 Hz")]
    [InlineData(165.002, "165 Hz")]
    public void FormatHzIsCultureInvariant(double hz, string expected)
    {
        Assert.Equal(expected, DisplayProfile.FormatHz(hz));
    }

    [Theory]
    [InlineData(1u, "Display.Connector.Vga")]
    [InlineData(5u, "Display.Connector.Dvi")]
    [InlineData(6u, "Display.Connector.Hdmi")]
    [InlineData(7u, "Display.Connector.Lvds")]
    [InlineData(10u, "Display.Connector.DisplayPort")]
    [InlineData(11u, "Display.Connector.DisplayPort")]
    [InlineData(17u, "Display.Connector.Virtual")]
    [InlineData(0u, "Display.Connector.Other")]
    [InlineData(0xFFFFFFFFu, "Display.Connector.Other")]
    public void ConnectorKeysFollowWingdi(uint tech, string key)
    {
        Assert.Equal(key, LiveDisplay.ConnectorResourceKey(tech));
    }

    private static MonitorSnapshot Snap() => new()
    {
        TargetDevicePath = @"\\?\DISPLAY#TEST#1",
        SourceWidth = 1920,
        SourceHeight = 1080,
        RefreshNumerator = 60,
        RefreshDenominator = 1,
        RefreshHz = 60,
    };
}

public class NormalizeRefreshTests
{
    [Fact]
    public void ExplicitRationalWins()
    {
        var m = new MonitorSnapshot { RefreshNumerator = 144000, RefreshDenominator = 1000, RefreshHz = 144 };
        Assert.Equal((144000u, 1000u), DisplayService.NormalizeRefresh(m));
    }

    [Fact]
    public void HandWrittenHzOverridesDefaultRational()
    {
        // 外部文件只写 refreshHz 时 Num/Den 是默认 60/1 —— 冲突时以 Hz 为准
        var m = new MonitorSnapshot { RefreshNumerator = 60, RefreshDenominator = 1, RefreshHz = 144 };
        Assert.Equal((144u, 1u), DisplayService.NormalizeRefresh(m));
    }

    [Fact]
    public void ConsistentDefaultsKept()
    {
        var m = new MonitorSnapshot { RefreshNumerator = 60, RefreshDenominator = 1, RefreshHz = 60 };
        Assert.Equal((60u, 1u), DisplayService.NormalizeRefresh(m));
    }

    [Fact]
    public void FractionalHzRationalized()
    {
        // Num/Den 显式缺省（0/0）：纯 Hz 输入按分母 1000 精确表达
        var m = new MonitorSnapshot { RefreshHz = 59.94, RefreshNumerator = 0, RefreshDenominator = 0 };
        Assert.Equal((59940u, 1000u), DisplayService.NormalizeRefresh(m));
    }

    [Fact]
    public void NothingSetFallsBackTo60()
    {
        Assert.Equal((60u, 1u), DisplayService.NormalizeRefresh(new MonitorSnapshot()));
    }
}
