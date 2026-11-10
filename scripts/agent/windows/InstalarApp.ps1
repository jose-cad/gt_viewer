# InstalarApp.ps1 - GtViewer Agent Script (Tarefa 14 / Spec 2.7)
# Executa instalação silenciosa com validações estritas de segurança
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
    # 1. Detecção prévia: se já instalado -> GTV-RESULT=JA_INSTALADO
    if (Test-Installed -tipo $RegraTipo -chave $RegraChave -valor $RegraValor) {
        Write-Output "Aplicativo já se encontra instalado no sistema."
        Write-Output "GTV-RESULT=JA_INSTALADO"
        Write-Output "GTV-EXIT=0"
        exit 0
    }

    # 2. Concorrência: se processo instalador anterior ainda estiver rodando -> GTV-RESULT=OCUPADO
    $busyProcesses = Get-Process -Name "msiexec" -ErrorAction SilentlyContinue
    if ($busyProcesses) {
        Write-Output "Processo de instalação do Windows (msiexec) já está em execução no momento."
        Write-Output "GTV-RESULT=OCUPADO"
        Write-Output "GTV-EXIT=1618"
        exit 1618
    }

    # 3. Diretório temporário isolado
    $tempDir = Join-Path $env:TEMP ("gtv_inst_" + [System.Guid]::NewGuid().ToString("N"))
    New-Item -ItemType Directory -Path $tempDir -Force | Out-Null
    $localPath = Join-Path $tempDir $FileName

    try {
        # 4. Download com suporte a retomada (Range) e token temporário de 30 min
        [System.Net.ServicePointManager]::SecurityProtocol = [System.Net.SecurityProtocolType]::Tls12 -bor [System.Net.SecurityProtocolType]::Tls13

        $downloaded = $false
        $attempts = 0
        while (-not $downloaded -and $attempts -lt 3) {
            $attempts++
            $httpClient = $null
            try {
                $existingBytes = 0
                if (Test-Path $localPath) {
                    $existingBytes = (Get-Item $localPath).Length
                }

                $httpClient = New-Object System.Net.Http.HttpClient
                $request = New-Object System.Net.Http.HttpRequestMessage([System.Net.Http.HttpMethod]::Get, $DownloadUrl)
                if (-not [string]::IsNullOrWhiteSpace($DownloadToken)) {
                    $request.Headers.Authorization = New-Object System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", $DownloadToken)
                }

                if ($existingBytes -gt 0) {
                    $request.Headers.Range = New-Object System.Net.Http.Headers.RangeHeaderValue($existingBytes, $null)
                }

                $response = $httpClient.SendAsync($request, [System.Net.Http.HttpCompletionOption]::ResponseHeadersRead).GetAwaiter().GetResult()

                if ($response.StatusCode -eq [System.Net.HttpStatusCode]::PartialContent) {
                    $stream = $response.Content.ReadAsStreamAsync().GetAwaiter().GetResult()
                    $fileStream = [System.IO.File]::Open($localPath, [System.IO.FileMode]::Append, [System.IO.FileAccess]::Write)
                    $stream.CopyTo($fileStream)
                    $fileStream.Close()
                    $stream.Close()
                    $downloaded = $true
                }
                elseif ($response.StatusCode -eq [System.Net.HttpStatusCode]::OK) {
                    $stream = $response.Content.ReadAsStreamAsync().GetAwaiter().GetResult()
                    $fileStream = [System.IO.File]::Create($localPath)
                    $stream.CopyTo($fileStream)
                    $fileStream.Close()
                    $stream.Close()
                    $downloaded = $true
                }
                else {
                    throw "Falha no download HTTP do instalador. Status: $($response.StatusCode)"
                }
            }
            catch {
                if ($attempts -ge 3) { throw }
                Start-Sleep -Seconds 2
            }
            finally {
                if ($httpClient) { $httpClient.Dispose() }
            }
        }

        # 5. Validação do Hash SHA-256 do instalador baixado
        $fileHash = (Get-FileHash -Path $localPath -Algorithm SHA256).Hash
        if ($fileHash -ne $ExpectedHash) {
            Write-Output "Hash SHA-256 do arquivo baixado ($fileHash) não confere com o esperado ($ExpectedHash)."
            Write-Output "GTV-RESULT=FALHA_HASH"
            Write-Output "GTV-EXIT=1"
            exit 1
        }

        # 6. Validação da assinatura digital Authenticode
        $sig = Get-AuthenticodeSignature -FilePath $localPath
        if ($sig.Status -ne "Valid") {
            Write-Output "Assinatura digital Authenticode inválida ou não assinada (Status: $($sig.Status))."
            Write-Output "GTV-RESULT=FALHA_ASSINATURA"
            Write-Output "GTV-EXIT=2"
            exit 2
        }

        # 7. Validação do fabricante do certificado digital
        $certSubject = $sig.SignerCertificate.Subject
        if ($certSubject -notmatch [regex]::Escape($ExpectedPublisher)) {
            Write-Output "Fabricante do certificado ('$certSubject') não confere com o esperado ('$ExpectedPublisher')."
            Write-Output "GTV-RESULT=FALHA_FABRICANTE"
            Write-Output "GTV-EXIT=3"
            exit 3
        }

        # 8. Execução silenciosa
        $ext = [System.IO.Path]::GetExtension($localPath).ToLowerInvariant()
        $exitCode = 0
        if ($ext -eq ".msi") {
            $msiArgs = "/i `"$localPath`" $InstallArgs"
            $p = Start-Process -FilePath "msiexec.exe" -ArgumentList $msiArgs -Wait -NoNewWindow -PassThru
            $exitCode = $p.ExitCode
        }
        else {
            $p = Start-Process -FilePath $localPath -ArgumentList $InstallArgs -Wait -NoNewWindow -PassThru
            $exitCode = $p.ExitCode
        }

        if ($exitCode -ne 0 -and $exitCode -ne 3010) {
            Write-Output "Processo de instalação retornou código de erro: $exitCode"
            Write-Output "GTV-RESULT=FALHA_INSTALACAO"
            Write-Output "GTV-EXIT=$exitCode"
            exit $exitCode
        }

        # 9. Pós-verificação: confirmação da regra de detecção
        if (Test-Installed -tipo $RegraTipo -chave $RegraChave -valor $RegraValor) {
            Write-Output "Instalação concluída e confirmada pela regra de detecção."
            Write-Output "GTV-RESULT=OK"
            Write-Output "GTV-EXIT=0"
            exit 0
        }
        else {
            Write-Output "Instalador finalizou mas regra de detecção não confirmou a instalação."
            Write-Output "GTV-RESULT=FALHA_DETECCAO"
            Write-Output "GTV-EXIT=4"
            exit 4
        }
    }
    finally {
        if (Test-Path $tempDir) {
            Remove-Item -Path $tempDir -Recurse -Force -ErrorAction SilentlyContinue
        }
    }
}
catch {
    Write-Output "Erro inesperado na instalação: $($_.Exception.Message)"
    Write-Output "GTV-RESULT=ERRO"
    Write-Output "GTV-EXIT=5"
    exit 5
}
