#requires -Version 5.1
<#
.SYNOPSIS
    Remove o item de menu de contexto do SubDownload e o executavel instalado.
#>

$ErrorActionPreference = 'Stop'

$installDir = Join-Path $env:LOCALAPPDATA 'SubDownload'

function Unregister-ContextMenu {
    param(
        [Parameter(Mandatory)] [string] $Extension,
        [Parameter(Mandatory)] [string] $KeyName
    )

    $keyPath = "HKCU:\Software\Classes\SystemFileAssociations\$Extension\shell\$KeyName"
    if (Test-Path $keyPath) {
        Remove-Item -Path $keyPath -Recurse -Force
        Write-Host "    Removido de $Extension" -ForegroundColor Green
    }
}

Write-Host "==> Removendo menu de contexto..." -ForegroundColor Cyan
foreach ($ext in '.mkv', '.mp4') {
    Unregister-ContextMenu -Extension $ext -KeyName 'SubDownload1BaixarPTBR'
    Unregister-ContextMenu -Extension $ext -KeyName 'SubDownload2BaixarAlts'
    Unregister-ContextMenu -Extension $ext -KeyName 'SubDownload3ClearAlts'
    # Nomes de chave de versoes anteriores do instalador.
    Unregister-ContextMenu -Extension $ext -KeyName 'SubDownloadPTBR'
    Unregister-ContextMenu -Extension $ext -KeyName 'SubDownloadClearAlts'
    Unregister-ContextMenu -Extension $ext -KeyName 'SubDownload2ClearAlts'
}

if (Test-Path $installDir) {
    Write-Host "==> Removendo $installDir ..." -ForegroundColor Cyan
    Remove-Item -Path $installDir -Recurse -Force
}

Write-Host "==> Reiniciando o Explorer para aplicar as mudancas..." -ForegroundColor Cyan
Stop-Process -Name explorer -Force -ErrorAction SilentlyContinue
Start-Sleep -Milliseconds 500
if (-not (Get-Process -Name explorer -ErrorAction SilentlyContinue)) {
    Start-Process explorer.exe
}

Write-Host ""
Write-Host "Desinstalacao concluida." -ForegroundColor Green
