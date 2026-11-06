<#
.SYNOPSIS
    DesbloquearMaquina.ps1 - Remove o bloqueio e restaura o estado original da máquina.
.DESCRIPTION
    Executado como NT AUTHORITY\SYSTEM via MeshCentral.
    1. Restaura as diretivas de logon local (USER_RIGHTS) a partir de original_sec.cfg ou padrão.
    2. Restaura ou limpa LegalNoticeCaption e LegalNoticeText.
    3. Remove as tarefas agendadas locais de persistência.
    4. Remove o marcador lock.marker.
    5. Encerra quaisquer sessões abertas de gtv-resgate.
    6. Retorna GTV-RESULT=DESBLOQUEADA e GTV-EXIT=0.
#>

$ErrorActionPreference = "Stop"
$Username = "gtv-resgate"
$DataDir = "$env:ProgramData\gt_viewer"
$MarkerFile = "$DataDir\lock.marker"
$OrigSecCfg = "$DataDir\original_sec.cfg"
$OrigNoticeJson = "$DataDir\original_notice.json"
$RestoreSdb = "$DataDir\restore.sdb"
$RegPath = "HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System"

try {
    # 1. Restaurar direitos de logon local via secedit
    if (Test-Path $OrigSecCfg) {
        secedit /configure /db "$RestoreSdb" /cfg "$OrigSecCfg" /areas USER_RIGHTS 2>$null | Out-Null
    } else {
        # Fallback para o padrão seguro do Windows caso não haja arquivo salvo
        $defaultInf = "$DataDir\default_sec.inf"
        $defaultContent = @"
[Unicode]
Unicode=yes
[Version]
signature="`$CHICAGO`$"
Revision=1
[Privilege Rights]
SeInteractiveLogonRight = *S-1-5-32-544,*S-1-5-32-545
SeRemoteInteractiveLogonRight = *S-1-5-32-544,*S-1-5-32-555
SeDenyInteractiveLogonRight =
SeDenyRemoteInteractiveLogonRight =
"@
        Set-Content -Path $defaultInf -Value $defaultContent -Force
        secedit /configure /db "$RestoreSdb" /cfg "$defaultInf" /areas USER_RIGHTS 2>$null | Out-Null
    }

    # 2. Restaurar mensagens legais do registro
    if (Test-Path $OrigNoticeJson) {
        try {
            $origNotice = Get-Content -Path $OrigNoticeJson -Raw | ConvertFrom-Json
            Set-ItemProperty -Path $RegPath -Name "LegalNoticeCaption" -Value ($origNotice.LegalNoticeCaption ?? "") -Force
            Set-ItemProperty -Path $RegPath -Name "LegalNoticeText" -Value ($origNotice.LegalNoticeText ?? "") -Force
        } catch {
            Set-ItemProperty -Path $RegPath -Name "LegalNoticeCaption" -Value "" -Force
            Set-ItemProperty -Path $RegPath -Name "LegalNoticeText" -Value "" -Force
        }
    } else {
        Set-ItemProperty -Path $RegPath -Name "LegalNoticeCaption" -Value "" -Force
        Set-ItemProperty -Path $RegPath -Name "LegalNoticeText" -Value "" -Force
    }

    # 3. Remover tarefas agendadas locais de reaplicação
    schtasks /delete /tn "GtvReapplyLock" /f 2>$null | Out-Null
    schtasks /delete /tn "GtvReapplyLockStartup" /f 2>$null | Out-Null

    # 4. Limpar marcadores e arquivos de bloqueio
    Remove-Item -Path $MarkerFile -Force -ErrorAction SilentlyContinue
    Remove-Item -Path "$DataDir\lock.*" -Force -ErrorAction SilentlyContinue

    # 5. Encerrar quaisquer sessões abertas da conta gtv-resgate
    $sessions = query user 2>$null
    if ($sessions) {
        foreach ($line in ($sessions -split "`r?`n" | Select-Object -Skip 1)) {
            if ($line -match "^\s*>?\s*$Username\s+(\S+)?\s+(\d+)\s+") {
                $sessId = $matches[2]
                logoff $sessId 2>$null
            }
        }
    }

    Write-Output "GTV-RESULT=DESBLOQUEADA"
    Write-Output "GTV-EXIT=0"
    exit 0
}
catch {
    Write-Output "GTV-RESULT=ERRO: $($_.Exception.Message)"
    Write-Output "GTV-EXIT=1"
    exit 1
}
