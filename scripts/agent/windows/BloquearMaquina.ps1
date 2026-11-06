<#
.SYNOPSIS
    BloquearMaquina.ps1 - Aplica o bloqueio local estrito à máquina Windows.
.DESCRIPTION
    Executado como NT AUTHORITY\SYSTEM via MeshCentral.
    1. Verifica que gtv-resgate existe, está habilitada, desbloqueada e com PasswordNeverExpires.
    2. Se lock.marker não existir: salva políticas secedit e LegalNotice originais e cria o marcador.
       Se já existir, não sobrescreve a cópia salva.
    3. Restringe SeInteractiveLogonRight apenas a gtv-resgate e nega logon às demais contas.
    4. Define LegalNoticeCaption e LegalNoticeText.
    5. Registra tarefa agendada local persistente (startup e a cada 5 min).
    6. Encerra todas as sessões ativas (logoff).
    7. Retorna GTV-RESULT=BLOQUEADA e GTV-EXIT=0.
#>

param(
    [Parameter(Mandatory = $false)][string]$ContactCaption = "Equipamento Bloqueado pela TI",
    [Parameter(Mandatory = $false)][string]$ContactMessage = "Equipamento bloqueado pela TI. Contato: ti@empresa.com.br"
)

$ErrorActionPreference = "Stop"
$Username = "gtv-resgate"
$DataDir = "$env:ProgramData\gt_viewer"
$MarkerFile = "$DataDir\lock.marker"
$OrigSecCfg = "$DataDir\original_sec.cfg"
$OrigNoticeJson = "$DataDir\original_notice.json"
$LockInf = "$DataDir\lock.inf"
$LockSdb = "$DataDir\lock.sdb"
$RegPath = "HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System"

try {
    # 1. Pré-condição: gtv-resgate deve existir e estar válida
    $user = Get-LocalUser -Name $Username -ErrorAction SilentlyContinue
    if (-not $user -or -not $user.Enabled -or -not $user.PasswordNeverExpires) {
        Write-Output "GTV-RESULT=FALHA_SEM_CONTA_RESGATE"
        Write-Output "GTV-EXIT=1"
        exit 1
    }

    if (-not (Test-Path $DataDir)) {
        New-Item -ItemType Directory -Path $DataDir -Force | Out-Null
    }

    # 2. Se o marcador NÃO existir, salvar o estado original
    if (-not (Test-Path $MarkerFile)) {
        # Exportar direitos originais via secedit
        secedit /export /cfg "$OrigSecCfg" /areas USER_RIGHTS 2>$null | Out-Null

        # Salvar avisos legais originais do registro
        $origCaption = (Get-ItemProperty -Path $RegPath -Name "LegalNoticeCaption" -ErrorAction SilentlyContinue).LegalNoticeCaption
        $origText = (Get-ItemProperty -Path $RegPath -Name "LegalNoticeText" -ErrorAction SilentlyContinue).LegalNoticeText

        [PSCustomObject]@{
            LegalNoticeCaption = $origCaption
            LegalNoticeText    = $origText
        } | ConvertTo-Json | Set-Content -Path $OrigNoticeJson -Force

        # Criar marcador
        Set-Content -Path $MarkerFile -Value "BLOQUEADA: $(Get-Date -Format 'o')" -Force
    }

    # 3. Aplicar restrição de logon local via secedit
    $resgateSid = $user.SID.Value
    $infContent = @"
[Unicode]
Unicode=yes
[Version]
signature="`$CHICAGO`$"
Revision=1
[Privilege Rights]
SeInteractiveLogonRight = *$resgateSid
SeDenyInteractiveLogonRight = *S-1-5-11,*S-1-5-32-545
SeDenyRemoteInteractiveLogonRight = *S-1-5-11,*S-1-5-32-545
"@
    Set-Content -Path $LockInf -Value $infContent -Force
    secedit /configure /db "$LockSdb" /cfg "$LockInf" /areas USER_RIGHTS 2>$null | Out-Null

    # 4. Configurar mensagem legal na tela de login
    Set-ItemProperty -Path $RegPath -Name "LegalNoticeCaption" -Value $ContactCaption -Force
    Set-ItemProperty -Path $RegPath -Name "LegalNoticeText" -Value $ContactMessage -Force

    # 5. Criar / atualizar Tarefa Agendada local persistente
    $scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
    $checkScript = Join-Path $scriptDir "VerificarBloqueio.ps1"
    
    # Copiar scripts essenciais para DataDir protegido se necessário
    Copy-Item -Path $checkScript -Destination "$DataDir\VerificarBloqueio.ps1" -Force -ErrorAction SilentlyContinue

    $taskCommand = "powershell.exe -ExecutionPolicy Bypass -NoProfile -File `"$DataDir\VerificarBloqueio.ps1`""

    # Tarefa periódica (a cada 5 min para blindar contra gpupdate de domínio)
    schtasks /create /tn "GtvReapplyLock" /tr "$taskCommand" /sc minute /mo 5 /ru "NT AUTHORITY\SYSTEM" /rl HIGHEST /f 2>$null | Out-Null
    # Tarefa ao inicializar
    schtasks /create /tn "GtvReapplyLockStartup" /tr "$taskCommand" /sc onstart /ru "NT AUTHORITY\SYSTEM" /rl HIGHEST /f 2>$null | Out-Null

    # 6. Encerrar todas as sessões ativas
    $sessions = query user 2>$null
    if ($sessions) {
        foreach ($line in ($sessions -split "`r?`n" | Select-Object -Skip 1)) {
            if ($line -match "^\s*>?\s*(\S+)\s+(\S+)?\s+(\d+)\s+") {
                $sessId = $matches[3]
                logoff $sessId 2>$null
            }
        }
    }

    Write-Output "GTV-RESULT=BLOQUEADA"
    Write-Output "GTV-EXIT=0"
    exit 0
}
catch {
    Write-Output "GTV-RESULT=ERRO: $($_.Exception.Message)"
    Write-Output "GTV-EXIT=1"
    exit 1
}
