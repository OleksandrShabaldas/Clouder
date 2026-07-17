using Clouder.Core.Models;

namespace Clouder.Storage;

public static class FileRuleEvaluator
{
    public static FileRule? FindMatch(
        IReadOnlyList<FileRule> rules, string fileName, long fileSize, string? folderPath = null)
    {
        foreach (var rule in rules.Where(r => r.IsEnabled).OrderByDescending(r => r.Priority))
        {
            if (Matches(rule, fileName, fileSize, folderPath))
                return rule;
        }
        return null;
    }

    private static bool Matches(FileRule rule, string fileName, long fileSize, string? folderPath) =>
        rule.Type switch
        {
            FileRuleType.FileExtension => MatchesExtensions(rule.Pattern, fileName),
            FileRuleType.MinFileSize => fileSize >= ParseSizeBytes(rule.Pattern),
            FileRuleType.MaxFileSize => fileSize <= ParseSizeBytes(rule.Pattern),
            FileRuleType.FolderPath => folderPath != null && MatchesPath(rule.Pattern, folderPath),
            FileRuleType.ExcludePattern => MatchesExtensions(rule.Pattern, fileName),
            _ => false
        };

    private static bool MatchesExtensions(string pattern, string fileName)
    {
        var parts = pattern.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        return parts.Any(p =>
        {
            var ext = p.TrimStart('*');
            return fileName.EndsWith(ext, StringComparison.OrdinalIgnoreCase);
        });
    }

    public static long ParseSizeBytes(string pattern)
    {
        var s = pattern.Trim().ToUpperInvariant();
        if (s.EndsWith("TB")) return long.Parse(s[..^2]) * 1024L * 1024 * 1024 * 1024;
        if (s.EndsWith("GB")) return long.Parse(s[..^2]) * 1024L * 1024 * 1024;
        if (s.EndsWith("MB")) return long.Parse(s[..^2]) * 1024L * 1024;
        if (s.EndsWith("KB")) return long.Parse(s[..^2]) * 1024L;
        if (s.EndsWith("B")) return long.Parse(s[..^1]);
        return long.Parse(s) * 1024L * 1024; // Default unit: MB
    }

    private static bool MatchesPath(string pattern, string path)
    {
        var normalized = pattern.Replace('/', '\\').TrimEnd('\\', '*');
        return path.Replace('/', '\\').StartsWith(normalized, StringComparison.OrdinalIgnoreCase);
    }
}
