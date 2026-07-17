using Clouder.Core.Email;
using Clouder.Core.Models;

namespace Clouder.Email;

public static class AlertPatternLibrary
{
    public static IReadOnlyList<EmailAlertPattern> GetPatterns() =>
    [
        // ── Security ────────────────────────────────────────────
        new()
        {
            PatternId = "security-alert",
            SenderAddresses =
            [
                "noreply@accounts.google.com",
                "security@google.com",
                "noreply@mega.nz",
                "security@mega.nz"
            ],
            SubjectKeywords =
            [
                "security alert",
                "suspicious sign-in",
                "unauthorized access",
                "new sign-in",
                "new login",
                "unusual activity",
                "sign-in attempt",
                "password changed",
                "recovery email changed",
                "someone has your password"
            ],
            BodyKeywords =
            [
                "unauthorized",
                "suspicious",
                "unrecognized device",
                "wasn't you"
            ],
            Severity = NotificationSeverity.Critical,
            Category = "Security"
        },

        // ── Account closure / inactivity ────────────────────────
        new()
        {
            PatternId = "account-closure",
            SenderAddresses =
            [
                "noreply@google.com",
                "no-reply@google.com",
                "noreply@accounts.google.com",
                "noreply@mega.nz"
            ],
            SubjectKeywords =
            [
                "account will be deleted",
                "account closure",
                "account suspended",
                "account disabled",
                "account deactivat",
                "inactive account",
                "inactivity",
                "verify your account",
                "confirm your account",
                "action required"
            ],
            BodyKeywords =
            [
                "will be deleted",
                "permanently closed",
                "due to inactivity",
                "suspended",
                "deactivated",
                "terminated"
            ],
            Severity = NotificationSeverity.Critical,
            Category = "Account"
        },

        // ── Storage quota ───────────────────────────────────────
        new()
        {
            PatternId = "storage-quota",
            SenderAddresses =
            [
                "noreply@google.com",
                "no-reply@google.com",
                "noreply@mega.nz"
            ],
            SubjectKeywords =
            [
                "storage is full",
                "storage is almost full",
                "running out of space",
                "storage limit",
                "quota exceeded",
                "upgrade your storage",
                "out of storage"
            ],
            BodyKeywords =
            [
                "storage quota",
                "no space left",
                "upgrade your plan",
                "storage full"
            ],
            Severity = NotificationSeverity.Warning,
            Category = "Storage"
        },

        // ── Terms / policy changes ──────────────────────────────
        new()
        {
            PatternId = "policy-change",
            SenderAddresses =
            [
                "noreply@google.com",
                "no-reply@google.com",
                "noreply@mega.nz"
            ],
            SubjectKeywords =
            [
                "terms of service",
                "privacy policy",
                "changes to",
                "policy update",
                "service update"
            ],
            BodyKeywords =
            [
                "terms of service",
                "privacy policy",
                "data processing"
            ],
            Severity = NotificationSeverity.Info,
            Category = "Policy"
        },

        // ── Data breach ─────────────────────────────────────────
        new()
        {
            PatternId = "data-breach",
            SenderAddresses =
            [
                "noreply@accounts.google.com",
                "noreply@google.com",
                "noreply@mega.nz"
            ],
            SubjectKeywords =
            [
                "data breach",
                "security incident",
                "compromised",
                "leaked",
                "exposed data"
            ],
            BodyKeywords =
            [
                "data breach",
                "security incident",
                "credentials exposed",
                "compromised"
            ],
            Severity = NotificationSeverity.Critical,
            Category = "Security"
        }
    ];

    public static readonly HashSet<string> KnownCloudSenders = new(StringComparer.OrdinalIgnoreCase)
    {
        "noreply@google.com",
        "no-reply@google.com",
        "noreply@accounts.google.com",
        "security@google.com",
        "drive-noreply@google.com",
        "noreply@mega.nz",
        "security@mega.nz",
        "support@mega.nz"
    };
}
