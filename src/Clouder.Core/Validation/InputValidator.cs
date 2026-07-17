namespace Clouder.Core.Validation;

public static class InputValidator
{
    public static (bool Valid, string? Error) ValidatePoolName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return (false, "Pool name is required.");
        if (name.Length > 100)
            return (false, "Pool name must be 100 characters or fewer.");
        if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            return (false, "Pool name contains invalid characters.");
        return (true, null);
    }

    public static (bool Valid, string? Error) ValidateLocalPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return (false, "Path is required.");
        if (!Path.IsPathFullyQualified(path))
            return (false, "Path must be an absolute path (e.g., C:\\Users\\You\\Clouder).");
        if (path.Length > 260)
            return (false, "Path is too long.");
        return (true, null);
    }

    public static (bool Valid, string? Error) ValidateRulePattern(string? pattern)
    {
        if (string.IsNullOrWhiteSpace(pattern))
            return (false, "Pattern is required.");
        return (true, null);
    }

    public static (bool Valid, string? Error) ValidateEmail(string? email)
    {
        if (string.IsNullOrWhiteSpace(email))
            return (false, "Email is required.");
        if (!email.Contains('@') || !email.Contains('.'))
            return (false, "Enter a valid email address.");
        return (true, null);
    }

    public static (bool Valid, string? Error) ValidateNotEmpty(string? value, string fieldName)
    {
        if (string.IsNullOrWhiteSpace(value))
            return (false, $"{fieldName} is required.");
        return (true, null);
    }
}
