# Script output {#top}

The complete output of one run of a [package script](help/script) on a server, as the agent reported it (Marc, 2026-11-13): every stream of PowerShell, one line per record with its time. Values of secret parameters are masked as ********. Reached with **Show output** in the **Script runs** box of the server's History tab. Every role may read it.

**Download .log** saves the output as a text file; **Open the script** opens the script page of the package.

## Result {#result}

**Result**: *Succeeded*, *Succeeded, restart* (3010 or 1641), *Failed* (with the exit code), *Timed out* (stopped with every process it started), *Blocked locally* (AllowScripts = 0 on the server) or *Not verified* (signature, hash, package, phase or an older revision did not match, so nothing ran), with revision, start, end and duration. **Output**: number of lines, errors and warnings, whether secret values were masked, and whether the output was cut in the middle to stay within 1 MB.

## Lines {#lines}

**Stream** filters the lines: all, or one of Output, Error, Warning, Verbose, Debug, Information, Host and Runner (the lines of the wrapper: PowerShell version, language mode, account, execution policy, the environment and the exit code). Errors name the line of the script. The agent keeps the same output for 14 days under `%ProgramData%\SUSyphos\Agent\Logs\Scripts`.
