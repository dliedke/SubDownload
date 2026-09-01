#requires -Version 5.1
<#
.SYNOPSIS
    Remove o item de menu de contexto do SubDownload e o executavel instalado.
#>

$ErrorActionPreference = 'Stop'

$installDir = Join-Path $env:LOCALAPPDATA 'SubDownload'

function Unregister-ContextMenu {
    param([Parameter(Mandatory)] [string] $Extension)

    $keyPath = "HKCU:\Software\Classes\SystemFileAssociations\$Extension\shell\SubDownloadPTBR"
    if (Test-Path $keyPath) {
        Remove-Item -Path $keyPath -Recurse -Force
        Write-Host "    Removido de $Extension" -ForegroundColor Green
    }
}

Write-Host "==> Removendo menu de contexto..." -ForegroundColor Cyan
Unregister-ContextMenu -Extension '.mkv'
Unregister-ContextMenu -Extension '.mp4'

if (Test-Path $installDir) {
    Write-Host "==> Removendo $installDir ..." -ForegroundColor Cyan
    Remove-Item -Path $installDir -Recurse -Force
}

Write-Host ""
Write-Host "Desinstalacao concluida." -ForegroundColor Green
