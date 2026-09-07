<#
.SYNOPSIS
    Downloads and bundles the Kokoro-82M runtime assets into the mod folder.

.DESCRIPTION
    Populates Resources\models (ONNX model + voice style vectors) and gives you the espeak-ng
    native library + data. The ONNX Runtime native DLLs (onnxruntime.dll / DirectML.dll) are NOT
    handled here — those are staged automatically by the .csproj build from the NuGet package.

    These files are intentionally kept out of git (see .gitignore); run this once after cloning,
    and again on any machine where you deploy the mod.

.PARAMETER Quantized
    Download the smaller q8f16 model (~86 MB) instead of the full fp32 model (~310 MB).

.EXAMPLE
    pwsh -File download-assets.ps1
    pwsh -File download-assets.ps1 -Quantized
#>
[CmdletBinding()]
param(
    [switch]$Quantized,
    [string]$EspeakZipUrl = "",
    [string[]]$Voices = @(),
    # Fetch every language's voices. Off by default: the mod's G2P is English, so only the
    # English voices (US "a*" + British "b*") are pulled unless you ask for all.
    [switch]$AllLanguages
)

$ErrorActionPreference = "Stop"
$ProgressPreference = "SilentlyContinue"   # much faster Invoke-WebRequest downloads

$root       = $PSScriptRoot
$modelsDir  = Join-Path $root "Resources\models"
$voicesDir  = Join-Path $modelsDir "voices"
$nativeDir  = Join-Path $root "Resources\native\win-x64"

New-Item -ItemType Directory -Force -Path $modelsDir, $voicesDir, $nativeDir | Out-Null

$hf = "https://huggingface.co/onnx-community/Kokoro-82M-v1.0-ONNX/resolve/main"

function Get-File($url, $dest) {
    if (Test-Path $dest) {
        Write-Host "  [skip] $(Split-Path $dest -Leaf) already present"
        return
    }
    Write-Host "  [get ] $url"
    Invoke-WebRequest -Uri $url -OutFile $dest
}

# ── 1. Model ────────────────────────────────────────────────────────────────
$modelFile = if ($Quantized) { "onnx/model_q8f16.onnx" } else { "onnx/model.onnx" }
Write-Host "Downloading Kokoro model ($modelFile)..."
Get-File "$hf/$modelFile" (Join-Path $modelsDir "kokoro-v1.0.onnx")

# ── 2. Voices ───────────────────────────────────────────────────────────────
# Each voice is a raw float32 [510,256] style bank. By default we fetch EVERY
# built-in Kokoro voice (all languages) by listing the repo; pass -Voices to
# restrict to a subset.
if (-not $Voices -or $Voices.Count -eq 0) {
    Write-Host "Listing built-in voices$(if ($AllLanguages) { ' (all languages)' } else { ' (English only)' })..."
    try {
        $tree = Invoke-RestMethod "https://huggingface.co/api/models/onnx-community/Kokoro-82M-v1.0-ONNX/tree/main/voices?recursive=false"
        $Voices = $tree | Where-Object { $_.path -like "voices/*.bin" } |
                  ForEach-Object { ($_.path -replace "voices/","") -replace "\.bin","" }
        if (-not $AllLanguages) {
            # English voices are the US "a*" and British "b*" prefixes.
            $Voices = $Voices | Where-Object { $_ -match '^[ab]' }
        }
    } catch {
        Write-Warning "Could not list voices from Hugging Face ($_). Falling back to the core English set."
        $Voices = @("af_heart","af_bella","af_nicole","af_sarah","am_michael","am_adam","am_puck","bf_emma","bm_george")
    }
}
Write-Host "Downloading $($Voices.Count) voices..."
foreach ($v in $Voices) {
    Get-File "$hf/voices/$v.bin" (Join-Path $voicesDir "$v.bin")
}

# ── 3. espeak-ng (native G2P) ────────────────────────────────────────────────
$espeakDll  = Join-Path $nativeDir "espeak-ng.dll"
$espeakData = Join-Path $nativeDir "espeak-ng-data"
if ((Test-Path $espeakDll) -and (Test-Path $espeakData)) {
    Write-Host "espeak-ng already present."
}
elseif ($EspeakZipUrl) {
    Write-Host "Downloading espeak-ng from $EspeakZipUrl ..."
    $tmp = Join-Path $env:TEMP "espeak-ng-bundle.zip"
    Invoke-WebRequest -Uri $EspeakZipUrl -OutFile $tmp
    Expand-Archive -Path $tmp -DestinationPath $nativeDir -Force
    Remove-Item $tmp -Force
    Write-Host "  Extracted. Verify espeak-ng.dll and espeak-ng-data\ landed in $nativeDir"
}
else {
    Write-Warning @"
espeak-ng not found and no -EspeakZipUrl given.
Place the Windows x64 build manually so these exist:
    $espeakDll
    $espeakData\   (the phoneme data folder)
Sources:
    - Official releases: https://github.com/espeak-ng/espeak-ng/releases  (install, then copy
      libespeak-ng.dll -> espeak-ng.dll and the espeak-ng-data folder)
    - Or pass a prebuilt zip URL:  download-assets.ps1 -EspeakZipUrl <url>
"@
}

Write-Host ""
Write-Host "Done. Model dir: $modelsDir"
Write-Host "Native dir:      $nativeDir"
Write-Host "Remember to build Source\RimSynapseLocalTts.csproj so the ONNX Runtime DLLs are staged."
