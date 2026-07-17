using Clouder.Core.Models;
using Clouder.Storage;

namespace Clouder.Storage.Tests;

public class FileRuleEvaluatorTests
{
    private static FileRule MakeRule(string id, FileRuleType type, string pattern,
        FileRuleAction action = FileRuleAction.RouteToAccount, int priority = 0, bool enabled = true) => new()
    {
        RuleId = id, Name = id, Type = type, Pattern = pattern,
        Action = action, Priority = priority, IsEnabled = enabled
    };

    [Theory]
    [InlineData("video.mp4", true)]
    [InlineData("video.MP4", true)]
    [InlineData("archive.zip", false)]
    [InlineData("photo.jpg", false)]
    public void FileExtension_MatchesCaseInsensitive(string fileName, bool shouldMatch)
    {
        var rules = new[] { MakeRule("r1", FileRuleType.FileExtension, "*.mp4, *.mkv") };
        var result = FileRuleEvaluator.FindMatch(rules, fileName, 1024);
        Assert.Equal(shouldMatch, result != null);
    }

    [Fact]
    public void MinFileSize_MatchesAboveThreshold()
    {
        var rules = new[] { MakeRule("r1", FileRuleType.MinFileSize, "100MB") };
        long mb100 = 100L * 1024 * 1024;

        Assert.NotNull(FileRuleEvaluator.FindMatch(rules, "big.bin", mb100));
        Assert.NotNull(FileRuleEvaluator.FindMatch(rules, "big.bin", mb100 + 1));
        Assert.Null(FileRuleEvaluator.FindMatch(rules, "small.bin", mb100 - 1));
    }

    [Fact]
    public void MaxFileSize_MatchesBelowThreshold()
    {
        var rules = new[] { MakeRule("r1", FileRuleType.MaxFileSize, "10MB") };
        long mb10 = 10L * 1024 * 1024;

        Assert.NotNull(FileRuleEvaluator.FindMatch(rules, "tiny.txt", mb10));
        Assert.NotNull(FileRuleEvaluator.FindMatch(rules, "tiny.txt", mb10 - 1));
        Assert.Null(FileRuleEvaluator.FindMatch(rules, "big.bin", mb10 + 1));
    }

    [Fact]
    public void FolderPath_MatchesPrefix()
    {
        var rules = new[] { MakeRule("r1", FileRuleType.FolderPath, "/Work/**") };

        Assert.NotNull(FileRuleEvaluator.FindMatch(rules, "doc.txt", 100, @"\Work\Reports"));
        Assert.NotNull(FileRuleEvaluator.FindMatch(rules, "doc.txt", 100, "/Work/Reports"));
        Assert.Null(FileRuleEvaluator.FindMatch(rules, "doc.txt", 100, @"\Personal\Photos"));
        Assert.Null(FileRuleEvaluator.FindMatch(rules, "doc.txt", 100)); // no folder
    }

    [Fact]
    public void ExcludePattern_MatchesTempFiles()
    {
        var rules = new[] { MakeRule("r1", FileRuleType.ExcludePattern, "*.tmp, *.bak",
            action: FileRuleAction.Exclude) };

        var match = FileRuleEvaluator.FindMatch(rules, "data.tmp", 100);
        Assert.NotNull(match);
        Assert.Equal(FileRuleAction.Exclude, match.Action);

        Assert.NotNull(FileRuleEvaluator.FindMatch(rules, "old.bak", 100));
        Assert.Null(FileRuleEvaluator.FindMatch(rules, "document.pdf", 100));
    }

    [Fact]
    public void Priority_HighestWins()
    {
        var rules = new[]
        {
            MakeRule("low", FileRuleType.FileExtension, "*.mp4", priority: 1),
            MakeRule("high", FileRuleType.FileExtension, "*.mp4", priority: 10),
            MakeRule("mid", FileRuleType.FileExtension, "*.mp4", priority: 5),
        };

        var match = FileRuleEvaluator.FindMatch(rules, "video.mp4", 1024);
        Assert.NotNull(match);
        Assert.Equal("high", match.RuleId);
    }

    [Fact]
    public void DisabledRules_AreSkipped()
    {
        var rules = new[]
        {
            MakeRule("disabled", FileRuleType.FileExtension, "*.mp4", priority: 10, enabled: false),
            MakeRule("enabled", FileRuleType.FileExtension, "*.mp4", priority: 1),
        };

        var match = FileRuleEvaluator.FindMatch(rules, "video.mp4", 1024);
        Assert.NotNull(match);
        Assert.Equal("enabled", match.RuleId);
    }

    [Fact]
    public void NoRules_ReturnsNull()
    {
        var match = FileRuleEvaluator.FindMatch([], "file.txt", 1024);
        Assert.Null(match);
    }

    [Theory]
    [InlineData("100MB", 100L * 1024 * 1024)]
    [InlineData("1GB", 1024L * 1024 * 1024)]
    [InlineData("512KB", 512L * 1024)]
    [InlineData("100", 100L * 1024 * 1024)]
    [InlineData("2TB", 2L * 1024 * 1024 * 1024 * 1024)]
    public void ParseSizeBytes_HandlesUnits(string input, long expected)
    {
        Assert.Equal(expected, FileRuleEvaluator.ParseSizeBytes(input));
    }
}
