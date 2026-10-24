# Remove o Cais: fecha o Dock (a barra de tarefas volta), tira da inicialização e apaga os arquivos.
# Uso: pwsh .\desinstalar.ps1                 (mantém suas configurações)
#      pwsh .\desinstalar.ps1 -ApagarConfig   (apaga também %APPDATA%\Cais)
param([switch]$ApagarConfig)

$destino = Join-Path $env:LOCALAPPDATA 'Programs\Cais'
$exe = Join-Path $destino 'Cais.exe'

if (Get-Process Cais -ErrorAction SilentlyContinue) {
    if (Test-Path $exe) { & $exe --sair }
    for ($i = 0; $i -lt 20 -and (Get-Process Cais -ErrorAction SilentlyContinue); $i++) { Start-Sleep -Milliseconds 250 }
    Get-Process Cais -ErrorAction SilentlyContinue | Stop-Process -Force
}

Remove-ItemProperty 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run' -Name 'Cais' -ErrorAction SilentlyContinue
Remove-Item (Join-Path ([Environment]::GetFolderPath('Programs')) 'Cais.lnk') -ErrorAction SilentlyContinue
Remove-Item $destino -Recurse -Force -ErrorAction SilentlyContinue
if ($ApagarConfig) { Remove-Item (Join-Path $env:APPDATA 'Cais') -Recurse -Force -ErrorAction SilentlyContinue }

Write-Host 'Cais removido. Se quiser a barra de tarefas sempre visível de novo:' -ForegroundColor Green
Write-Host 'Configurações > Personalização > Barra de tarefas > Comportamentos > desmarque "Ocultar automaticamente".'
