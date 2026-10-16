param([Parameter(Mandatory = $true)][string]$JobFile)
$ErrorActionPreference = 'Stop'
$expectedRoot = [IO.Path]::GetFullPath((Join-Path $env:LOCALAPPDATA 'Cmcoordinator\Updates'))
$jobPath = [IO.Path]::GetFullPath($JobFile)
if (-not $jobPath.StartsWith($expectedRoot + '\', [StringComparison]::OrdinalIgnoreCase)) { exit 2 }
$job = Get-Content -LiteralPath $jobPath -Raw -Encoding UTF8 | ConvertFrom-Json
$installer = [IO.Path]::GetFullPath($job.Installer)
if ([IO.Path]::GetDirectoryName($installer) -ne [IO.Path]::GetDirectoryName($jobPath)) { exit 2 }
$pending = Join-Path $expectedRoot 'pending.json'
$log = Join-Path $expectedRoot 'update.log'
$lease = $null
function Save-State([string]$state, [string]$detail) {
    $value = @{ State=$state; Tag=$job.Tag; HelperPid=$PID; Detail=$detail; UpdatedAt=[DateTime]::UtcNow.ToString('o') }
    $value | ConvertTo-Json | Set-Content -LiteralPath $pending -Encoding UTF8
    ('{0} {1} {2}' -f [DateTime]::UtcNow.ToString('o'), $state, $detail) | Add-Content -LiteralPath $log -Encoding UTF8
}
try {
    $lease = [IO.File]::Open((Join-Path $expectedRoot 'installation.lock'), 'OpenOrCreate', 'ReadWrite', 'None')
} catch { exit 3 }
try {
    Save-State 'waiting' 'Esperando el cierre voluntario de todas las instancias de Revit.'
    $deadline = [DateTime]::UtcNow.AddHours(24)
    while (Get-Process -Name Revit -ErrorAction SilentlyContinue) {
        if ([DateTime]::UtcNow -gt $deadline) { throw 'La espera superó 24 horas; vuelve a pulsar Actualizar.' }
        Start-Sleep -Seconds 2
    }
    if ((Get-FileHash -LiteralPath $installer -Algorithm SHA256).Hash -ne $job.Sha256) { throw 'SHA256 del instalador no coincide.' }
    Save-State 'installing' 'Iniciando el instalador oficial; puede solicitar permisos de administrador.'
    # Installer is interactive so the user can select components and approve Windows UAC.
    # Its own Revit process guard also checks for a restart during this transition.
    $process = Start-Process -FilePath $installer -Verb RunAs -Wait -PassThru
    if ($process.ExitCode -ne 0) { throw ('Instalador cancelado o fallido: ' + $process.ExitCode) }
    $assemblyHash = (Get-FileHash -LiteralPath $job.Assembly -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($assemblyHash -eq $job.PreviousHash) { throw 'No cambió el complemento de esta instalación de Revit. Revisa los componentes seleccionados.' }
    $receipt = [IO.Path]::GetFullPath($job.Receipt)
    if ([IO.Path]::GetDirectoryName($receipt) -ne $expectedRoot) { throw 'Ruta de recibo inválida.' }
    @{ Tag=$job.Tag; Key=$job.Key; AssemblyHash=$assemblyHash; InstalledAt=[DateTime]::UtcNow.ToString('o') } |
        ConvertTo-Json | Set-Content -LiteralPath $receipt -Encoding UTF8
    Save-State 'completed' 'Actualización instalada. Puedes abrir Revit.'
} catch {
    Save-State 'failed' $_.Exception.Message
    exit 1
} finally {
    if ($null -ne $lease) { $lease.Dispose() }
}
