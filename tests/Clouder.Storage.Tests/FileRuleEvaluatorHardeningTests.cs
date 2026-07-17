using Clouder.Core.Models;
using Clouder.Storage;

namespace Clouder.Storage.Tests;

public class FileRuleEvaluatorHardeningTests
{
    [Theory]
    [InlineData("1.5GB", 1_610_612_736L)]
    [InlineData("0.5MB", 524_288L)]
    [InlineData("2GB", 2_147_483_648L)]
    [InlineData("100", 104_857_600L)] // no suffix → MB
    public void ParseSizeBytes_SupportsDecimals(string pattern, long expected)
    {
        Assert.Equal(expected, FileRuleEvaluator.ParseSizeBytes(pattern));
    }

    [Fact]
    public void FindMatch_SkipsRuleWithInvalidPattern_AndTriesNextRule()
    {
        var rules = new List<FileRule>
        {
            new()
            {
                RuleId = "broken", Name = "Broken size rule",
                Type = FileRuleType.MinFileSize, Pattern = "not-a-size",
                Action = FileRuleAction.Exclude, Priority = 100, IsEnabled = true
            },
            new()
            {
                RuleId = "good", Name = "Extension rule",
                Type = FileRuleType.FileExtension, Pattern = "*.mp4",
                Action = FileRuleAction.Exclude, Priority = 50, IsEnabled = true
            }
        };

        // The broken high-priority rule must not throw or block evaluation.
        var match = FileRuleEvaluator.FindMatch(rules, "movie.mp4", 1024);

        Assert.NotNull(match);
        Assert.Equal("good", match.RuleId);
    }
}
