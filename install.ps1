#requires -Version 5.1
<#
.SYNOPSIS
    Instala o SubDownload e registra os itens "Baixar Legenda (PT-BR)",
    "Baixar Legendas Alternativas" e "Limpar Legendas Alternativas (.1, .2)"
    no menu de contexto do Explorer para arquivos .mkv e .mp4.

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
        [Parameter(Mandatory)] [string] $ExePath,
        [Parameter(Mandatory)] [string] $KeyName,     # ex: "SubDownloadPTBR"
        [Parameter(Mandatory)] [string] $MenuText,    # ex: "Baixar Legenda (PT-BR)"
        [string] $ExtraArgs = ''                      # ex: "--clean-alts"
    )

    $keyPath = "HKCU:\Software\Classes\SystemFileAssociations\$Extension\shell\$KeyName"
    $cmdPath = "$keyPath\command"

    New-Item -Path $keyPath -Force | Out-Null
    Set-ItemProperty -Path $keyPath -Name '(default)' -Value $MenuText
    Set-ItemProperty -Path $keyPath -Name 'Icon' -Value "$ExePath,0"

    $commandLine = if ($ExtraArgs) { "`"$ExePath`" $ExtraArgs `"%1`"" } else { "`"$ExePath`" `"%1`"" }

    New-Item -Path $cmdPath -Force | Out-Null
    Set-ItemProperty -Path $cmdPath -Name '(default)' -Value $commandLine

    Write-Host "    Registrado '$MenuText' para $Extension" -ForegroundColor Green
}

Write-Host "==> Registrando menu de contexto (HKCU, sem precisar de admin)..." -ForegroundColor Cyan
# O Explorer ordena os itens pelo nome da chave do registro (ordem alfabetica),
# entao o prefixo numerico abaixo garante a ordem: Baixar Legenda -> Baixar
# Legendas Alternativas -> Limpar Legendas Alternativas.
foreach ($ext in '.mkv', '.mp4') {
    Register-ContextMenu -Extension $ext -ExePath $exePath `
        -KeyName 'SubDownload1BaixarPTBR' -MenuText 'Baixar Legenda (PT-BR)'
    Register-ContextMenu -Extension $ext -ExePath $exePath `
        -KeyName 'SubDownload2BaixarAlts' -MenuText 'Baixar Legendas Alternativas' `
        -ExtraArgs '--download-alts'
    Register-ContextMenu -Extension $ext -ExePath $exePath `
        -KeyName 'SubDownload3ClearAlts' -MenuText 'Limpar Legendas Alternativas (.1, .2)' `
        -ExtraArgs '--clean-alts'

    # Remove chaves antigas (versoes anteriores do instalador), se existirem.
    foreach ($oldKeyName in 'SubDownloadPTBR', 'SubDownloadClearAlts', 'SubDownload2ClearAlts') {
        $oldKeyPath = "HKCU:\Software\Classes\SystemFileAssociations\$ext\shell\$oldKeyName"
        if (Test-Path $oldKeyPath) {
            Remove-Item -Path $oldKeyPath -Recurse -Force
        }
    }
}

Write-Host "==> Reiniciando o Explorer para aplicar as mudancas..." -ForegroundColor Cyan
Stop-Process -Name explorer -Force -ErrorAction SilentlyContinue
Start-Sleep -Milliseconds 500
if (-not (Get-Process -Name explorer -ErrorAction SilentlyContinue)) {
    Start-Process explorer.exe
}

Write-Host ""
Write-Host "Instalacao concluida!" -ForegroundColor Green
Write-Host "Clique com o botao direito em um arquivo .mkv ou .mp4 e escolha 'Baixar Legenda (PT-BR)'."
Write-Host "Se a principal estiver fora de sincronia, escolha 'Baixar Legendas Alternativas'."
Write-Host "Para limpar as alternativas baixadas (.1.srt, .2.srt), escolha 'Limpar Legendas Alternativas (.1, .2)'."
Write-Host "Para desinstalar, execute .\uninstall.ps1"
