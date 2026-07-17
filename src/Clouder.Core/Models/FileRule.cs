namespace Clouder.Core.Models;

public enum FileRuleType
{
    FileExtension,
    MinFileSize,
    MaxFileSize,
    FolderPath,
    ExcludePattern
}

public enum FileRuleAction
{
    RouteToAccount,
    RouteToProvider,
    UseStrategy,
    Exclude
}

public sealed class FileRule
{
    public required string RuleId { get; set; }
    public string? PoolId { get; set; }
    public required string Name { get; set; }
    public FileRuleType Type { get; set; }
    public required string Pattern { get; set; }
    public FileRuleAction Action { get; set; }
    public string? TargetAccountId { get; set; }
    public string? TargetProviderId { get; set; }
    public PlacementStrategy? OverrideStrategy { get; set; }
    public int Priority { get; set; }
    public bool IsEnabled { get; set; } = true;
}
