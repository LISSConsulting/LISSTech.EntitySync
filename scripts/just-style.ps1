function Test-JustGlyphSupport {
    -not [Console]::IsOutputRedirected -and (
        $env:WT_SESSION -or
        $env:TERM_PROGRAM -or
        $env:TERM -match 'xterm|screen|tmux|cygwin|ansi' -or
        $Host.Name -match 'Visual Studio Code'
    )
}

function Write-JustStep {
    param(
        [string]$Icon,
        [string]$Fallback,
        [string]$Text,
        [ConsoleColor]$ForegroundColor = 'Cyan'
    )

    $label = if (Test-JustGlyphSupport) { $Icon } else { $Fallback }
    Write-Host ('[{0}] {1} {2}' -f (Get-Date -Format 'HH:mm:ss'), $label, $Text) -ForegroundColor $ForegroundColor
}

function Invoke-JustTimed {
    param(
        [string]$Icon,
        [string]$Fallback,
        [string]$Text,
        [scriptblock]$Script
    )

    Write-JustStep -Icon $Icon -Fallback $Fallback -Text $Text
    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    try {
        & $Script
        $sw.Stop()
        Write-JustStep -Icon '✅' -Fallback '[ok]' -Text ('{0} ({1:N1}s)' -f $Text, $sw.Elapsed.TotalSeconds) -ForegroundColor Green
    }
    catch {
        $sw.Stop()
        Write-JustStep -Icon '❌' -Fallback '[fail]' -Text ('{0} failed after {1:N1}s' -f $Text, $sw.Elapsed.TotalSeconds) -ForegroundColor Red
        throw
    }
}
