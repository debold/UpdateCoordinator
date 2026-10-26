namespace SUSyphos.Server.Administration;

/// <summary>
/// One minimum for every password the dashboard sets (accounts, first administrator, backups), so the server check and
/// the hint while typing (app.js reads the minlength attribute) never differ. Length over composition rules (NIST
/// SP 800-63B): long passphrases are encouraged, weak patterns only get a hint (Marc, 2026-10-26).
/// </summary>
internal static class PasswordRules
{
    public const int MinimumLength = 12;
}
