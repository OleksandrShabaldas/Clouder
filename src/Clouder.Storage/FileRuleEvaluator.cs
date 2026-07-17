using System.Globalization;
using Clouder.Core.Logging;
using Clouder.Core.Models;

namespace Clouder.Storage;

public static class FileRuleEvaluator
{
    public static FileRule? FindMatch(
        IReadOnlyList<FileRule> rules, string fileName, long fileSize, string? folderPath = null)
    {
        foreach (var rule in rules.Where(r => r.IsEnabled).OrderByDescending(r => r.Priority))
        {
            try
            {
                if (Matches(rule, fileName, fileSize, folderPath))
                    return rule;
            }
            catch (Exception ex)
            {
                // A malformed pattern must never abort placement — skip the rule.
                ClouderLog.Warn($"File rule '{rule.Name}' has an invalid pattern '{rule.Pattern}' — skipped ({ex.Message})");
            }
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
        if (s.EndsWith("TB")) return Mul(s[..^2], 1024L * 1024 * 1024 * 1024);
        if (s.EndsWith("GB")) return Mul(s[..^2], 1024L * 1024 * 1024);
        if (s.EndsWith("MB")) return Mul(s[..^2], 1024L * 1024);
        if (s.EndsWith("KB")) return Mul(s[..^2], 1024L);
        if (s.EndsWith("B")) return Mul(s[..^1], 1L);
        return Mul(s, 1024L * 1024); // Default unit: MB
    }

    // Decimal sizes like "1.5GB" are valid.
    private static long Mul(string number, long unit) =>
        (long)(double.Parse(number, NumberStyles.Float, CultureInfo.InvariantCulture) * unit);

    private static bool MatchesPath(string pattern, string path)
    {
        var normalized = pattern.Replace('/', '\\').TrimEnd('\\', '*');
        return path.Replace('/', '\\').StartsWith(normalized, StringComparison.OrdinalIgnoreCase);
    }
}
