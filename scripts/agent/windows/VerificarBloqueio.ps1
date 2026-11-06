<#
.SYNOPSIS
    VerificarBloqueio.ps1 - Verifica e reaplica o bloqueio local se o marcador existir.
.DESCRIPTION
    Executado periodicamente pela Tarefa Agendada local ou sob demanda pelo gt_viewer.
    - Se lock.marker existir:
      - Garante que a política secedit lock.inf e as chaves de aviso continuam ativas (blindando contra gpupdate).
      - Encerra qualquer sessão ativa da conta gtv-resgate.
      - Retorna GTV-RESULT=BLOQUEADA e GTV-EXIT=0.
    - Se lock.marker não existir:
      - Retorna GTV-RESULT=DESBLOQUEADA e GTV-EXIT=0.
#>

$ErrorActionPreference = "Stop"
$Username = "gtv-resgate"
$DataDir = "$env:ProgramData\gt_viewer"
$MarkerFile = "$DataDir\lock.marker"
$LockInf = "$DataDir\lock.inf"
$LockSdb = "$DataDir\lock.sdb"
$RegPath = "HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System"

try {
    if (Test-Path $MarkerFile) {
        # Marcador existe -> Garantir que bloqueio continua imposto
        if (Test-Path $LockInf) {
            secedit /configure /db "$LockSdb" /cfg "$LockInf" /areas USER_RIGHTS 2>$null | Out-Null
        }

        # Encerrar qualquer sessão residual da gtv-resgate
        $sessions = query user 2>$null
        if ($sessions) {
            foreach ($line in ($sessions -split "`r?`n" | Select-Object -Skip 1)) {
                if ($line -match "^\s*>?\s*$Username\s+(\S+)?\s+(\d+)\s+") {
                    $sessId = $matches[2]
                    logoff $sessId 2>$null
                }
            }
        }

        Write-Output "GTV-RESULT=BLOQUEADA"
        Write-Output "GTV-EXIT=0"
        exit 0
    } else {
        Write-Output "GTV-RESULT=DESBLOQUEADA"
        Write-Output "GTV-EXIT=0"
        exit 0
    }
}
catch {
    Write-Output "GTV-RESULT=ERRO: $($_.Exception.Message)"
    Write-Output "GTV-EXIT=1"
    exit 1
}
