using SUSyphos.Contracts.Scripts;

namespace SUSyphos.Server.Components;

/// <summary>How scripts of WinGet packages read in the dashboard (Marc, 2026-11-13): phases, options and run results.</summary>
public static class ScriptText
{
    public static string Phase(ScriptPhase phase) => phase switch
    {
        ScriptPhase.BeforeInstall => "Before installation",
        ScriptPhase.AfterInstall => "After installation",
        ScriptPhase.BeforeUninstall => "Before uninstallation",
        _ => "After uninstallation",
    };

    /// <summary>The address part of a phase on the script page ("before-install").</summary>
    public static string Slug(ScriptPhase phase) => phase switch
    {
        ScriptPhase.BeforeInstall => "before-install",
        ScriptPhase.AfterInstall => "after-install",
        ScriptPhase.BeforeUninstall => "before-uninstall",
        _ => "after-uninstall",
    };

    public static ScriptPhase? FromSlug(string? slug) => slug switch
    {
        "before-install" => ScriptPhase.BeforeInstall,
        "after-install" => ScriptPhase.AfterInstall,
        "before-uninstall" => ScriptPhase.BeforeUninstall,
        "after-uninstall" => ScriptPhase.AfterUninstall,
        _ => null,
    };

    public static string Link(string packageId, ScriptPhase phase) => $"{PackagePage.Link(packageId)}/scripts/{Slug(phase)}";

    public static string RunOn(ScriptRunOn runOn) => runOn switch
    {
        ScriptRunOn.Install => "New installations only",
        ScriptRunOn.Upgrade => "Upgrades only",
        _ => "Installations and upgrades",
    };

    public static string OnFailure(ScriptPhase phase, ScriptOnFailure onFailure) => (phase, onFailure) switch
    {
        (ScriptPhase.BeforeInstall, ScriptOnFailure.Continue) => "Install anyway",
        (ScriptPhase.BeforeInstall, _) => "Do not install",
        (ScriptPhase.BeforeUninstall, _) => "Do not uninstall",
        _ => "Count as failed",
    };

    public static (string Label, string Class) Result(ScriptRunResult result) => result switch
    {
        ScriptRunResult.Succeeded => ("Succeeded", "ok"),
        ScriptRunResult.SucceededRestart => ("Succeeded, restart", "queue"),
        ScriptRunResult.TimedOut => ("Timed out", "fail"),
        ScriptRunResult.Blocked => ("Blocked locally", "fail"),
        ScriptRunResult.NotVerified => ("Not verified", "fail"),
        _ => ("Failed", "fail"),
    };

    /// <summary>The tag class of a stream in the output table.</summary>
    public static int StreamIndex(string stream) => stream switch
    {
        "Error" => 4,
        "Warning" => 3,
        "Runner" => 0,
        "Verbose" or "Debug" => 7,
        _ => 1,
    };

    public static string RunLink(Guid clientId, Guid runId) => $"/servers/{clientId:D}/script-runs/{runId:D}";
}
