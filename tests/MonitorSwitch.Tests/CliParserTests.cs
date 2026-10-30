using Xunit;
using MonitorSwitch.Cli;
using MonitorSwitch.Models;

namespace MonitorSwitch.Tests;

public class CliParserTests
{
    [Theory]
    [InlineData(new[] { "list" }, true)]
    [InlineData(new[] { "APPLY", "work" }, true)]
    [InlineData(new[] { "set", "1", "refresh", "144" }, true)]
    [InlineData(new[] { "--minimized" }, false)]
    [InlineData(new[] { "--show-flyout" }, false)]
    [InlineData(new string[0], false)]
    [InlineData(new[] { "unknownverb" }, false)]
    public void IsCliInvocationDetectsVerbs(string[] args, bool expected)
    {
        Assert.Equal(expected, CliHost.IsCliInvocation(args));
    }

    [Fact]
    public void ParseSplitsFlagsAndPositionals()
    {
        var req = CliHost.Parse(new[] { "save", "gaming", "--replace", "--json" });
        Assert.Equal("save", req.Command);
        Assert.Equal(new[] { "gaming" }, req.Args);
        Assert.True(req.Flags.ContainsKey("replace"));
        Assert.True(req.Json);
    }

    [Fact]
    public void ParseEmptyArgsAsksForHelp()
    {
        Assert.Throws<CliUsageException>(() => CliHost.Parse(Array.Empty<string>()));
    }

    [Theory]
    [InlineData("2560x1440", 2560u, 1440u)]
    [InlineData("1920X1080", 1920u, 1080u)]
    [InlineData("3840*2160", 3840u, 2160u)]
    [InlineData("2560×1440", 2560u, 1440u)]
    [InlineData("2560,1440", 2560u, 1440u)]
    public void ParseSizeAcceptsCommonForms(string value, uint w, uint h)
    {
        Assert.Equal((w, h), CliHost.ParseSize(value));
    }

    [Theory]
    [InlineData("abc")]
    [InlineData("2560")]
    [InlineData("0x800")]
    [InlineData("2560x0")]
    [InlineData("2560x-1440")]
    public void ParseSizeRejectsGarbage(string value)
    {
        Assert.Throws<CliUsageException>(() => CliHost.ParseSize(value));
    }

    [Theory]
    [InlineData("144", 144u, 1u, 144.0)]
    [InlineData("59.94", 59940u, 1000u, 59.94)]
    [InlineData("165.002", 165u, 1u, 165.002)] // 与整数差 <0.01 时按整数吸附（与草稿/匹配逻辑同一容差约定）
    public void ParseRefreshRationalizes(string value, uint num, uint den, double hz)
    {
        Assert.Equal((num, den, hz), CliHost.ParseRefresh(value));
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-5")]
    [InlineData("999")]
    [InlineData("abc")]
    public void ParseRefreshRejectsOutOfRange(string value)
    {
        Assert.Throws<CliUsageException>(() => CliHost.ParseRefresh(value));
    }

    [Theory]
    [InlineData("0", 1u)]
    [InlineData("90", 2u)]
    [InlineData("180", 3u)]
    [InlineData("270", 4u)]
    [InlineData("landscape", 1u)]
    [InlineData("portrait", 2u)]
    [InlineData("portrait-flipped", 4u)]
    public void ParseRotationMaps(string value, uint expected)
    {
        Assert.Equal(expected, CliHost.ParseRotation(value));
    }

    [Fact]
    public void ParseRotationRejectsUnknown()
    {
        Assert.Throws<CliUsageException>(() => CliHost.ParseRotation("45"));
    }

    [Fact]
    public void ParsePositionAcceptsNegative()
    {
        Assert.Equal((-1920, -541), CliHost.ParsePosition("-1920,-541"));
    }

    [Theory]
    [InlineData("on", true)]
    [InlineData("1", true)]
    [InlineData("enable", true)]
    [InlineData("off", false)]
    [InlineData("0", false)]
    [InlineData("disabled", false)]
    public void ParseBoolVariants(string value, bool expected)
    {
        Assert.Equal(expected, CliHost.ParseBool("hdr", value));
    }

    [Fact]
    public void ParseBoolRejectsOtherText()
    {
        Assert.Throws<CliUsageException>(() => CliHost.ParseBool("hdr", "maybe"));
    }

    [Fact]
    public void ParsePairsWalksKeyValueSequence()
    {
        var pairs = CliHost.ParsePairs(new List<string> { "1", "refresh", "144", "hdr", "on" });
        Assert.Equal(new[] { "refresh", "hdr" }, pairs.Select(p => p.Key));
        Assert.Equal(new[] { "144", "on" }, pairs.Select(p => p.Value));
    }

    [Fact]
    public void ParsePairsRejectsOddCount()
    {
        Assert.Throws<CliUsageException>(() => CliHost.ParsePairs(new List<string> { "1", "refresh" }));
    }

    [Fact]
    public void DisplayWidthCountsCjkAsTwo()
    {
        Assert.Equal(4, CliHost.DisplayWidth("work"));
        Assert.Equal(6, CliHost.DisplayWidth("配置 1")); // 4 + 空格1 + 1
        Assert.Equal(8, CliHost.DisplayWidth("中文测试"));
    }
}
