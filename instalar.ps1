# Compila e instala o Cais em %LOCALAPPDATA%\Programs\Cais, cria o atalho no menu Iniciar e abre o Dock.
# Uso: pwsh .\instalar.ps1            (ou: powershell -ExecutionPolicy Bypass -File .\instalar.ps1)
#      pwsh .\instalar.ps1 -NaoAbrir  (só instala)
param([switch]$NaoAbrir)
$ErrorActionPreference = 'Stop'

$raiz = $PSScriptRoot
$destino = Join-Path $env:LOCALAPPDATA 'Programs\Cais'
$saida = Join-Path $raiz 'bin\Release\net48'

Write-Host 'Compilando o Cais...' -ForegroundColor Cyan
dotnet build (Join-Path $raiz 'Cais.csproj') -c Release -nologo -v quiet
if ($LASTEXITCODE -ne 0) { throw 'A compilação falhou. Você precisa do .NET SDK instalado: https://dotnet.microsoft.com/download' }

# Fecha a versão que estiver rodando (ela devolve a barra de tarefas ao sair)
$rodando = @(Get-Process Cais -ErrorAction SilentlyContinue | Select-Object -ExpandProperty Id)
if ($rodando.Count -gt 0) {
    Write-Host 'Fechando a versão em execução...'
    Start-Process (Join-Path $saida 'Cais.exe') -ArgumentList '--sair' -Wait
    foreach ($id in $rodando) {
        try { Wait-Process -Id $id -Timeout 10 -ErrorAction Stop }
        catch { Stop-Process -Id $id -Force -ErrorAction SilentlyContinue; Wait-Process -Id $id -Timeout 5 -ErrorAction SilentlyContinue }
    }
}

Write-Host "Copiando para $destino..."
New-Item -ItemType Directory -Force $destino | Out-Null
for ($tentativa = 1; ; $tentativa++) {
    try { Copy-Item (Join-Path $saida '*') $destino -Recurse -Force -ErrorAction Stop; break }
    catch {
        if ($tentativa -ge 10) { throw }
        Start-Sleep -Milliseconds 500   # o Windows às vezes demora um instante para liberar o .exe
    }
}

$atalho = Join-Path ([Environment]::GetFolderPath('Programs')) 'Cais.lnk'
$shell = New-Object -ComObject WScript.Shell
$lnk = $shell.CreateShortcut($atalho)
$lnk.TargetPath = Join-Path $destino 'Cais.exe'
$lnk.WorkingDirectory = $destino
$lnk.Description = 'Cais - dock para Windows'
$lnk.Save()

Write-Host 'Cais instalado.' -ForegroundColor Green
Write-Host 'Dica: abra o Cais de novo pelo menu Iniciar para ver as configurações, ou clique com o botão direito no Dock.'
if (-not $NaoAbrir) { Start-Process (Join-Path $destino 'Cais.exe') }
