using SUSyphos.Contracts.Agents;

namespace SUSyphos.Server.Assignment;

/// <summary>Form values and labels of reboot policies in the dashboard.</summary>
internal static class RebootPolicyText
{
    /// <summary>Form value for "inherit from groups or the system default".</summary>
    public const string Inherit = "";

    public static string Label(RebootPolicy policy) => policy switch
    {
        RebootPolicy.InsideMaintenanceWindow => "Reboot inside the maintenance window",
        _ => "Never reboot (report only)",
    };

    /// <summary>Parses a form value: empty means inherit (null); anything but a defined policy name is invalid.</summary>
    public static bool TryParse(string? value, out RebootPolicy? policy)
    {
        policy = null;
        if (string.IsNullOrEmpty(value))
        {
            return true;
        }

        if (Enum.TryParse<RebootPolicy>(value, ignoreCase: false, out var parsed) && Enum.IsDefined(parsed) && !int.TryParse(value, out _))
        {
            policy = parsed;
            return true;
        }

        return false;
    }
}
