#requires -Version 5.1
<#
.SYNOPSIS
    Instala o SubDownload e registra o item "Baixar Legenda (PT-BR)" no menu de
    contexto do Explorer para arquivos .mkv e .mp4.

.DESCRIPTION
    - Publica o app como um executavel unico, autocontido (nao exige .NET
      instalado na maquina) em win-x64.
    - Copia o executavel para %LOCALAPPDATA%\SubDownload\SubDownload.exe.
    - Registra o menu de contexto em HKCU (nao precisa de administrador).

.NOTES
    Execute a partir da raiz do repositorio: .\install.ps1
#>

$ErrorActionPreference = 'Stop'

$root       = $PSScriptRoot
$projectDir = Join-Path $root 'src\SubDownload'
$installDir = Join-Path $env:LOCALAPPDATA 'SubDownload'
$exeName    = 'SubDownload.exe'

Write-Host "==> Publicando SubDownload (self-contained, win-x64)..." -ForegroundColor Cyan
$publishDir = Join-Path $projectDir 'bin\publish'

dotnet publish $projectDir `
    -c Release `
    -r win-x64 `
    --self-contained true `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -o $publishDir

if ($LASTEXITCODE -ne 0) {
    throw "Falha ao publicar o projeto (dotnet publish retornou $LASTEXITCODE)."
}

Write-Host "==> Instalando em $installDir ..." -ForegroundColor Cyan
New-Item -ItemType Directory -Force -Path $installDir | Out-Null
Copy-Item -Path (Join-Path $publishDir $exeName) -Destination $installDir -Force

$exePath = Join-Path $installDir $exeName
if (-not (Test-Path $exePath)) {
    throw "Executavel nao encontrado apos a publicacao: $exePath"
}

function Register-ContextMenu {
    param(
        [Parameter(Mandatory)] [string] $Extension,   # ex: ".mkv"
        [Parameter(Mandatory)] [string] $ExePath
    )

    $keyPath = "HKCU:\Software\Classes\SystemFileAssociations\$Extension\shell\SubDownloadPTBR"
    $cmdPath = "$keyPath\command"

    New-Item -Path $keyPath -Force | Out-Null
    Set-ItemProperty -Path $keyPath -Name '(default)' -Value 'Baixar Legenda (PT-BR)'
    Set-ItemProperty -Path $keyPath -Name 'Icon' -Value "$ExePath,0"

    New-Item -Path $cmdPath -Force | Out-Null
    Set-ItemProperty -Path $cmdPath -Name '(default)' -Value "`"$ExePath`" `"%1`""

    Write-Host "    Registrado para $Extension" -ForegroundColor Green
}

Write-Host "==> Registrando menu de contexto (HKCU, sem precisar de admin)..." -ForegroundColor Cyan
Register-ContextMenu -Extension '.mkv' -ExePath $exePath
Register-ContextMenu -Extension '.mp4' -ExePath $exePath

Write-Host ""
Write-Host "Instalacao concluida!" -ForegroundColor Green
Write-Host "Clique com o botao direito em um arquivo .mkv ou .mp4 e escolha 'Baixar Legenda (PT-BR)'."
Write-Host "Para desinstalar, execute .\uninstall.ps1"
