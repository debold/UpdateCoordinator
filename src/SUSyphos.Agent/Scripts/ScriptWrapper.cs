namespace SUSyphos.Agent.Scripts;

/// <summary>
/// The fixed wrapper every script runs in (Marc, 2026-11-13: all streams logged): it reads the parameters from standard
/// input, offers <c>Get-SUSyphosParameter</c>, runs the script with every stream merged (<c>*&gt;&amp;1</c>) and writes
/// each record as one line with UTC time and stream; errors carry line and command. Header: PowerShell version,
/// language mode, account and execution policy; footer: exit code. No Start-Transcript; group policy transcription and
/// script block logging stay as the machine has them.
/// </summary>
internal static class ScriptWrapper
{
    public const string FileName = "susyphos-wrapper.ps1";

    public const string Text = """
        param([Parameter(Mandatory = $true)][string]$ScriptPath)
        $ErrorActionPreference = 'Continue'
        $VerbosePreference = 'Continue'
        $DebugPreference = 'Continue'
        $InformationPreference = 'Continue'
        $ProgressPreference = 'SilentlyContinue'
        [Console]::OutputEncoding = New-Object System.Text.UTF8Encoding($false)
        function Write-SUSyphosRecord([string]$Stream, [string]$Text) {
            $now = [DateTime]::UtcNow.ToString('yyyy-MM-ddTHH:mm:ss.fffZ')
            foreach ($line in ($Text -split "`r?`n")) { [Console]::Out.WriteLine(('{0} [{1}] {2}' -f $now, $Stream, $line)) }
        }
        $global:SUSyphosValues = @{}
        $raw = [Console]::In.ReadToEnd()
        if ($raw) { (ConvertFrom-Json $raw).PSObject.Properties | ForEach-Object { $global:SUSyphosValues[$_.Name] = [string]$_.Value } }
        function global:Get-SUSyphosParameter {
            param([Parameter(Mandatory = $true)][string]$Name)
            if ($global:SUSyphosValues.ContainsKey($Name)) { $global:SUSyphosValues[$Name] } else { $null }
        }
        Write-SUSyphosRecord 'Runner' ('PowerShell {0}, language mode {1}, account {2}, execution policy {3}' -f $PSVersionTable.PSVersion, $ExecutionContext.SessionState.LanguageMode, [Security.Principal.WindowsIdentity]::GetCurrent().Name, (Get-ExecutionPolicy))
        Write-SUSyphosRecord 'Runner' ('Action {0}, package {1}, version {2}, previous version {3}, architecture {4}, installer exit code {5}' -f $env:SUSYPHOS_ACTION, $env:SUSYPHOS_PACKAGE_ID, $env:SUSYPHOS_VERSION, $env:SUSYPHOS_PREVIOUS_VERSION, $env:SUSYPHOS_ARCHITECTURE, $env:SUSYPHOS_INSTALLER_EXIT_CODE)
        $global:LASTEXITCODE = 0
        $code = 0
        $started = [DateTime]::UtcNow
        try {
            & $ScriptPath *>&1 | ForEach-Object {
                $record = $_
                if ($record -is [System.Management.Automation.ErrorRecord]) {
                    $where = ([string]$record.ScriptStackTrace -split "`r?`n")[0]
                    if (-not $where) { $where = 'line {0}' -f $record.InvocationInfo.ScriptLineNumber }
                    Write-SUSyphosRecord 'Error' ('{0} ({1})' -f $record.ToString(), $where)
                } elseif ($record -is [System.Management.Automation.WarningRecord]) {
                    Write-SUSyphosRecord 'Warning' $record.Message
                } elseif ($record -is [System.Management.Automation.VerboseRecord]) {
                    Write-SUSyphosRecord 'Verbose' $record.Message
                } elseif ($record -is [System.Management.Automation.DebugRecord]) {
                    Write-SUSyphosRecord 'Debug' $record.Message
                } elseif ($record -is [System.Management.Automation.InformationRecord]) {
                    $stream = 'Information'
                    if ($record.Tags -contains 'PSHOST') { $stream = 'Host' }
                    Write-SUSyphosRecord $stream ([string]$record.MessageData)
                } else {
                    Write-SUSyphosRecord 'Output' (($record | Out-String -Width 4096).TrimEnd())
                }
            }
            if ($LASTEXITCODE -ne $null -and $LASTEXITCODE -ne 0) { $code = $LASTEXITCODE }
        } catch {
            Write-SUSyphosRecord 'Error' ('{0} (line {1})' -f $_.ToString(), $_.InvocationInfo.ScriptLineNumber)
            $code = 1
        }
        Write-SUSyphosRecord 'Runner' ('Exit code {0} after {1} s' -f $code, ([DateTime]::UtcNow - $started).TotalSeconds.ToString('0.0', [Globalization.CultureInfo]::InvariantCulture))
        exit $code
        """;
}
