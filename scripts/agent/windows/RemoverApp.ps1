# RemoverApp.ps1 - GtViewer Agent Script (Tarefa 14 / Spec 2.7)
# Executa desinstalação silenciosa com validações estritas de segurança
$ErrorActionPreference = "Stop"

function Test-Installed {
    param([string]$tipo, [string]$chave, [string]$valor)
    try {
        if ($tipo -eq "arquivo") {
            return (Test-Path -Path $chave -PathType Leaf)
        }
        elseif ($tipo -eq "registro") {
            if (-not (Test-Path -Path $chave)) { return $false }
            if ([string]::IsNullOrWhiteSpace($valor)) { return $true }
            $prop = Get-ItemProperty -Path $chave -ErrorAction SilentlyContinue
            if ($null -eq $prop) { return $false }
            foreach ($p in $prop.PSObject.Properties) {
                if ($p.Value -and $p.Value.ToString() -like "*$valor*") {
                    return $true
                }
            }
            return $false
        }
        return $false
    }
    catch {
        return $false
    }
}

try {
    # 1. Detecção prévia: se NÃO estiver instalado -> sucesso ("não estava instalado")
    if (-not (Test-Installed -tipo $RegraTipo -chave $RegraChave -valor $RegraValor)) {
        Write-Output "Aplicativo não se encontra instalado no sistema."
        Write-Output "GTV-RESULT=NAO_ESTAVA_INSTALADO"
        Write-Output "GTV-EXIT=0"
        exit 0
    }

    # 2. Executa comando de desinstalação
    if ([string]::IsNullOrWhiteSpace($UninstallCmd)) {
        Write-Output "Comando de desinstalação não definido."
        Write-Output "GTV-RESULT=FALHA_REMOCAO"
        Write-Output "GTV-EXIT=1"
        exit 1
    }

    $p = Start-Process -FilePath "cmd.exe" -ArgumentList "/c $UninstallCmd" -Wait -NoNewWindow -PassThru
    if ($p.ExitCode -ne 0 -and $p.ExitCode -ne 3010) {
        Write-Output "Comando de desinstalação retornou código de erro: $($p.ExitCode)"
        Write-Output "GTV-RESULT=FALHA_REMOCAO"
        Write-Output "GTV-EXIT=$($p.ExitCode)"
        exit $($p.ExitCode)
    }

    # 3. Pós-verificação: confirma que NÃO está mais instalado
    if (-not (Test-Installed -tipo $RegraTipo -chave $RegraChave -valor $RegraValor)) {
        Write-Output "Aplicativo removido com sucesso."
        Write-Output "GTV-RESULT=OK"
        Write-Output "GTV-EXIT=0"
        exit 0
    }
    else {
        Write-Output "Comando finalizou mas regra de detecção ainda encontrou o aplicativo."
        Write-Output "GTV-RESULT=FALHA_REMOCAO"
        Write-Output "GTV-EXIT=2"
        exit 2
    }
}
catch {
    Write-Output "Erro inesperado durante desinstalação: $($_.Exception.Message)"
    Write-Output "GTV-RESULT=ERRO"
    Write-Output "GTV-EXIT=3"
    exit 3
}
