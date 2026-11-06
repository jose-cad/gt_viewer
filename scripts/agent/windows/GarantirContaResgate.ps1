<#
.SYNOPSIS
    GarantirContaResgate.ps1 - Garante a existência e prontidão da conta gtv-resgate.
.DESCRIPTION
    Executado como NT AUTHORITY\SYSTEM via MeshCentral.
    - Idempotente.
    - Cria ou garante o usuário local 'gtv-resgate'.
    - Adiciona ao grupo Administradores (resolvido por SID S-1-5-32-544).
    - Configura PasswordNeverExpires = $true, UserMayNotChangePassword = $true, Enabled = $true.
    - Executa Unlock-LocalUser.
    - Define a senha (recebida diretamente ou via token /agente/*).
    - Se $Revealed for $true, encerra qualquer sessão aberta de 'gtv-resgate'.
    - Imprime marcadores GTV-RESULT e GTV-EXIT.
#>

param(
    [Parameter(Mandatory = $false)][string]$Password,
    [Parameter(Mandatory = $false)][string]$PasswordToken,
    [Parameter(Mandatory = $false)][string]$ServerUrl,
    [Parameter(Mandatory = $false)][switch]$Revealed
)

$ErrorActionPreference = "Stop"
$Username = "gtv-resgate"

try {
    # 1. Obter a senha se fornecida por token HTTPS (/agente/*)
    if ([string]::IsNullOrWhiteSpace($Password) -and -not [string]::IsNullOrWhiteSpace($PasswordToken) -and -not [string]::IsNullOrWhiteSpace($ServerUrl)) {
        $headers = @{ "Authorization" = "Bearer $PasswordToken" }
        $Password = (Invoke-RestMethod -Uri "$ServerUrl/agente/senha-resgate" -Headers $headers -Method GET -UseBasicParsing).Trim()
    }

    if ([string]::IsNullOrWhiteSpace($Password)) {
        Write-Output "GTV-RESULT=FALHA_SENHA_NAO_INFORMADA"
        Write-Output "GTV-EXIT=1"
        exit 1
    }

    # 2. Verificar se o usuário já existe
    $user = Get-LocalUser -Name $Username -ErrorAction SilentlyContinue

    if (-not $user) {
        # Criação do usuário
        $secPassword = ConvertTo-SecureString $Password -AsPlainText -Force
        $user = New-LocalUser -Name $Username `
                              -Password $secPassword `
                              -Description "Conta de resgate emergencial do gt_viewer" `
                              -PasswordNeverExpires `
                              -UserMayNotChangePassword
    } else {
        # Atualização da senha e flags existentes
        $secPassword = ConvertTo-SecureString $Password -AsPlainText -Force
        Set-LocalUser -Name $Username `
                      -Password $secPassword `
                      -PasswordNeverExpires $true `
                      -UserMayNotChangePassword $true
    }

    # 3. Garantir conta habilitada e desbloqueada
    Enable-LocalUser -Name $Username -ErrorAction SilentlyContinue
    Unlock-LocalUser -Name $Username -ErrorAction SilentlyContinue

    # 4. Adicionar ao grupo Administradores (resolvido agnosticamente por SID S-1-5-32-544)
    $adminSid = New-Object System.Security.Principal.SecurityIdentifier("S-1-5-32-544")
    $adminGroup = $adminSid.Translate([System.Security.Principal.NTAccount]).Value
    # Obter apenas o nome simples do grupo (ex: Administradores)
    $adminGroupName = ($adminGroup -split "\\")[-1]

    try {
        Add-LocalGroupMember -Group $adminGroupName -Member $Username -ErrorAction SilentlyContinue
    } catch {
        # Fallback via net localgroup caso o cmdlet tenha limitação de escopo
        net localgroup "$adminGroupName" "$Username" /add 2>$null
    }

    # 5. Se a senha estava revelada, derrubar imediatamente qualquer sessão ativa de gtv-resgate
    if ($Revealed) {
        $sessions = query user 2>$null
        if ($sessions) {
            foreach ($line in ($sessions -split "`r?`n" | Select-Object -Skip 1)) {
                if ($line -match "^\s*>?\s*$Username\s+(\S+)?\s+(\d+)\s+") {
                    $sessId = $matches[2]
                    logoff $sessId 2>$null
                }
            }
        }
    }

    # 6. Validação final de saúde da conta
    $checkUser = Get-LocalUser -Name $Username
    if ($checkUser.Enabled -and $checkUser.PasswordNeverExpires) {
        Write-Output "GTV-RESULT=OK"
        Write-Output "GTV-EXIT=0"
        exit 0
    } else {
        Write-Output "GTV-RESULT=FALHA_ESTADO_INVALIDO"
        Write-Output "GTV-EXIT=2"
        exit 2
    }
}
catch {
    Write-Output "GTV-RESULT=ERRO: $($_.Exception.Message)"
    Write-Output "GTV-EXIT=1"
    exit 1
}
