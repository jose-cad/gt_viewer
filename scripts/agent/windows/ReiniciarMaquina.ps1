<#
.SYNOPSIS
    ReiniciarMaquina.ps1 - Dispara reinício do computador com aviso prévio de 2 minutos ao usuário (J7).
.DESCRIPTION
    Executado como NT AUTHORITY\SYSTEM via agente MeshCentral.
    Exibe a mensagem padrão na área de trabalho:
    "A TI vai reiniciar seu computador em 2 minutos. Salve seu trabalho."
    Agenda o reinício com timeout de 120 segundos via shutdown.exe com encerramento forçado de processos (/f).
    Retorna GTV-RESULT=REINICIANDO e GTV-EXIT=0.
#>

$ErrorActionPreference = "Stop"

try {
    $Message = "A TI vai reiniciar seu computador em 2 minutos. Salve seu trabalho."

    # Cancela reinício anterior pendente se houver, evitando conflito de agendamento (código 1190)
    & shutdown.exe /a 2>$null | Out-Null

    # Agenda reinício para 2 minutos (120 segundos) com mensagem e fechamento forçado
    & shutdown.exe /r /t 120 /c $Message /f
    if ($LASTEXITCODE -ne 0) {
        Write-Output "GTV-RESULT=ERRO: shutdown.exe retornou codigo $LASTEXITCODE"
        Write-Output "GTV-EXIT=$LASTEXITCODE"
        exit $LASTEXITCODE
    }

    Write-Output "GTV-RESULT=REINICIANDO"
    Write-Output "GTV-EXIT=0"
    exit 0
}
catch {
    Write-Output "GTV-RESULT=ERRO: $($_.Exception.Message)"
    Write-Output "GTV-EXIT=1"
    exit 1
}
