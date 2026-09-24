# Builds the release: artifacts\HamMeter-Setup-<version>.exe
#   1. publish the app (self-contained folder, installed to Program Files)
#   2. compile the invisible Inno Setup engine around it
#   3. hash the engine (the setup verifies it before running it)
#   4. publish the setup UI as one exe with the engine embedded
param(
    [string]$Version = "0.1.0"
)

$ErrorActionPreference = "Stop"
$root = Split-Path $PSScriptRoot -Parent
$out = Join-Path $root "artifacts"

if (Get-Process -Name "HamMeter-Setup*" -ErrorAction SilentlyContinue) {
    throw "A HamMeter setup is running. Close it first, then build again."
}
if (Test-Path $out) { Remove-Item $out -Recurse -Force }
New-Item -ItemType Directory $out | Out-Null

# Every release needs patch notes: they become "What's new" and the GitHub release text.
$changelog = Get-Content (Join-Path $root "CHANGELOG.md") -Raw
$escaped = [regex]::Escape($Version)
$section = [regex]::Match($changelog, "(?ms)^## v?$escaped\b[^\r\n]*\r?\n(.*?)(?=^## |\z)")
if (-not $section.Success) { throw "CHANGELOG.md has no entry '## $Version - yyyy-mm-dd'. Add the patch notes first." }

$iscc = @(
    "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe",
    "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
    "$env:ProgramFiles\Inno Setup 6\ISCC.exe"
) | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $iscc) { throw "Inno Setup 6 not found (winget install JRSoftware.InnoSetup)." }

Write-Host "==> Publishing HamMeter $Version"
dotnet publish "$root\HamMeter.Aion2\HamMeter.Aion2.csproj" -c Release -r win-x64 --self-contained true `
    -p:Version=$Version -p:DebugType=none -o "$out\app"
if ($LASTEXITCODE -ne 0) { throw "App publish failed." }

Write-Host "==> Compiling installer engine"
& $iscc /Q "/DAppDir=$out\app" "/DAppVersion=$Version" "/O$out" "$root\installer\HamMeter.iss"
if ($LASTEXITCODE -ne 0) { throw "Inno Setup compile failed." }

$core = Join-Path $out "HamMeter-Core.exe"
(Get-FileHash $core -Algorithm SHA256).Hash | Set-Content -NoNewline -Encoding ascii "$core.sha256"

Write-Host "==> Publishing setup"
# Trimmed: the wizard uses a small slice of .NET, trimming cuts the setup UI from ~38 to ~13 MB.
dotnet publish "$root\HamMeter.Setup\HamMeter.Setup.csproj" -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:PublishTrimmed=true -p:TrimMode=full `
    -p:Version=$Version -p:DebugType=none "-p:PayloadPath=$core" -o "$out\setup"
if ($LASTEXITCODE -ne 0) { throw "Setup publish failed." }

$final = Join-Path $out "HamMeter-Setup-$Version.exe"
Copy-Item "$out\setup\HamMeter-Setup.exe" $final
$hash = (Get-FileHash $final -Algorithm SHA256).Hash
Set-Content -Encoding ascii "$final.sha256" "$hash  HamMeter-Setup-$Version.exe"

# Sign with the release key (DPAPI, this Windows account). HamMeter only installs an
# update whose .sig matches the public key built into it.
Write-Host "==> Signing"
dotnet run --project "$root\build\ReleaseSigner" -c Release -- sign $final
if ($LASTEXITCODE -ne 0) { throw "Signing failed." }

# Release text for GitHub: the changelog section (keeps the [important] marker).
$heading = [regex]::Match($changelog, "(?m)^## v?$escaped\b[^\r\n]*").Value
$important = if ($heading -match "\[important\]") { "[important]`n`n" } else { "" }
Set-Content -Encoding utf8 (Join-Path $out "release-notes-$Version.md") ($important + $section.Groups[1].Value.Trim())

Write-Host ""
Write-Host "Done: $final ($([math]::Round((Get-Item $final).Length / 1MB, 1)) MB)"
Write-Host "SHA-256: $hash"
Write-Host "Upload to the GitHub release 'v$Version': HamMeter-Setup-$Version.exe and HamMeter-Setup-$Version.exe.sig"
Write-Host "Release text: artifacts\release-notes-$Version.md"
