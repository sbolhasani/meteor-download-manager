param([switch]$UsePrebuiltNative, [string]$PrebuiltNativePath, [switch]$SkipInstaller)
$ErrorActionPreference = "Stop"
Set-Location (Join-Path $PSScriptRoot "..")
$version = (Get-Content VERSION).Trim()

function Resolve-Torrent([string]$Source) {
    if (-not $Source) { $Source = ".native-cache\sdk\win-x64" }
    if (-not (Test-Path -LiteralPath $Source)) {
        throw "No prebuilt native package at '$Source'. Supply a trusted Windows Portable ZIP via -PrebuiltNativePath."
    }
    $path = (Resolve-Path -LiteralPath $Source).Path
    $item = Get-Item -LiteralPath $path
    $archive = $null
    if ($item.PSIsContainer) {
        $root = $path
    } elseif ($item.Extension -ieq ".zip") {
        $archive = $path
        $hash = (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash.ToLowerInvariant()
        $root = Join-Path ".native-cache\prebuilt-win-x64" $hash
        $marker = Join-Path $root ".ready"
        if (-not (Test-Path -LiteralPath $marker)) {
            if (Test-Path -LiteralPath $root) { Remove-Item -LiteralPath $root -Recurse -Force }
            New-Item -ItemType Directory -Path $root -Force | Out-Null
            Expand-Archive -LiteralPath $archive -DestinationPath $root -Force
            New-Item -ItemType File -Path $marker -Force | Out-Null
        }
    } else {
        throw "-PrebuiltNativePath must be a Portable ZIP or extracted Portable/SDK directory."
    }
    $dlls = @(Get-ChildItem -LiteralPath $root -Filter "nexonic_torrent.dll" -File -Recurse)
    if ($dlls.Count -ne 1) { throw "Expected exactly one nexonic_torrent.dll, found $($dlls.Count)." }
    $dll = $dlls[0].FullName
    $dllDir = Split-Path $dll
    $sdkDir = Split-Path $dllDir
    $licenses = @((Join-Path $root "torrent-licenses"), (Join-Path $root "notices"),
                  (Join-Path $sdkDir "notices"), (Join-Path $dllDir "torrent-licenses")) |
                Where-Object { Test-Path -LiteralPath $_ -PathType Container } | Select-Object -First 1
    if (-not $licenses) { throw "Missing torrent license notices. Supply a complete Portable ZIP or SDK folder." }
    $metadata = @((Join-Path $root "torrent-components.json"), (Join-Path $root "native-components.json"),
                  (Join-Path $sdkDir "native-components.json"), (Join-Path $dllDir "torrent-components.json")) |
                Where-Object { Test-Path -LiteralPath $_ -PathType Leaf } | Select-Object -First 1
    if ($metadata) {
        $manifest = Get-Content -LiteralPath $metadata -Raw | ConvertFrom-Json
        if ($manifest.target -and $manifest.target -ne "win-x64") { throw "Native target mismatch: $($manifest.target)" }
    }
    Write-Host "Native torrent DLL: $dll"
    Write-Host "SHA-256: $((Get-FileHash -LiteralPath $dll -Algorithm SHA256).Hash)"
    return @{ Dll = $dll; Licenses = $licenses; Metadata = $metadata; Archive = $archive }
}
if ($PrebuiltNativePath -and -not $UsePrebuiltNative) { throw "-PrebuiltNativePath requires -UsePrebuiltNative." }
if ($UsePrebuiltNative) {
    $native = Resolve-Torrent $PrebuiltNativePath
} else {
    python scripts/build-torrent-native.py win-x64
    if ($LASTEXITCODE -ne 0) { throw "Native torrent engine build failed" }
    $native = Resolve-Torrent ".native-cache\sdk\win-x64"
}
$out = "build\win"
if (Test-Path build) { Remove-Item build -Recurse -Force }
# The app and the browser helper (also the ndm command) share one .NET runtime in the app folder, as on Linux:
# the helper adds only its own files instead of carrying a second copy of the runtime.
$common = @("-c", "Release", "-r", "win-x64", "--self-contained", "true", "-p:Version=$version")
dotnet publish windows\App\App.csproj @common -o $out
if ($LASTEXITCODE -ne 0) { throw "App build failed" }
dotnet publish windows\Host\Host.csproj @common -o build\host
if ($LASTEXITCODE -ne 0) { throw "Helper build failed" }
foreach ($f in "nexonic-host.exe", "nexonic-host.dll", "nexonic-host.deps.json", "nexonic-host.runtimeconfig.json") {
  Copy-Item "build\host\$f" $out
}
# The helper uses the app's copy of the shared code; both come from the same build.
if ((Get-FileHash build\host\Nexonic.Core.dll).Hash -ne (Get-FileHash "$out\Nexonic.Core.dll").Hash) { throw "Helper and app were built from different code" }
Remove-Item build\host -Recurse -Force
Get-ChildItem $out -Filter *.pdb | Remove-Item
# Small bundled ffmpeg (re-packs streamed video into .mp4), cross-built on Linux by scripts/build-ffmpeg.sh windows.
if (Test-Path .ffmpeg-cache\windows\ffmpeg.exe) {
  Copy-Item .ffmpeg-cache\windows\ffmpeg.exe, .ffmpeg-cache\windows\ffprobe.exe $out
} else {
  Write-Warning "No .ffmpeg-cache\windows\ffmpeg.exe: streamed video will be saved without joining picture and sound"
}
# Video engine for YouTube and other sites (scripts/fetch-video-tools.sh windows .tools-cache/windows).
if (Test-Path .tools-cache\windows\yt-dlp\yt-dlp.exe) {
  # The unpacked engine folder (starts at once; the one-file edition unpacked itself before every video), its program
  # renamed nexonic-video-engine.exe.
  Copy-Item .tools-cache\windows\yt-dlp "$out\video-engine" -Recurse
  Rename-Item "$out\video-engine\yt-dlp.exe" "nexonic-video-engine.exe"
  Copy-Item .tools-cache\windows\qjs.exe $out
  Get-Content packaging\THIRD-PARTY-NOTICES.txt, .ffmpeg-cache\windows\NOTICE-video-tools-1.txt, .tools-cache\windows\NOTICE-video-tools-2.txt |
    Set-Content "$out\THIRD-PARTY-NOTICES.txt"
} else {
  Write-Warning "No .tools-cache\windows\yt-dlp\yt-dlp.exe: videos from YouTube and other sites will not work in this build"
}
Copy-Item -LiteralPath $native.Dll -Destination $out -Force
Copy-Item -LiteralPath $native.Licenses -Destination "$out\torrent-licenses" -Recurse -Force
if ($native.Metadata) {
    Copy-Item -LiteralPath $native.Metadata -Destination "$out\torrent-components.json" -Force
} else {
    @{
        engine = "libtorrent"
        target = "win-x64"
        origin = "prebuilt"
        source = if ($native.Archive) { Split-Path -Leaf $native.Archive } else { "local portable directory" }
        sha256 = (Get-FileHash -LiteralPath $native.Dll -Algorithm SHA256).Hash.ToLowerInvariant()
    } | ConvertTo-Json | Set-Content -Path "$out\torrent-components.json" -Encoding UTF8
}
Copy-Item packaging\TERMS.txt $out
python scripts\build-extensions.py
if ($LASTEXITCODE -ne 0) { throw "Extension build failed" }
Copy-Item build\extension-chrome "$out\Extension" -Recurse
Copy-Item "build\NexonicFirefoxAddon-$version.xpi" "$out\Firefox Add-on.xpi"
# The ndm command runs the helper in command-line mode; the installer puts this folder on the user's PATH.
Set-Content -Path "$out\ndm.cmd" -Value "@echo off`r`n`"%~dp0nexonic-host.exe`" %*" -Encoding ascii -NoNewline
Compress-Archive -Path "$out\*" -DestinationPath "build\MeteorDownloadManager-$version-windows-portable.zip"
if ($SkipInstaller) {
    Write-Host "Built portable ZIP: build\MeteorDownloadManager-$version-windows-portable.zip"
} else {
    $iscc = Join-Path ([Environment]::GetFolderPath("ProgramFilesX86")) "Inno Setup 6\ISCC.exe"
    if (-not (Test-Path -LiteralPath $iscc)) {
        throw "Inno Setup 6 not installed. Use -SkipInstaller; the portable ZIP was already created."
    }
    $env:NDM_VERSION = $version
    & $iscc windows\installer.iss
    if ($LASTEXITCODE -ne 0) { throw "Installer build failed" }
    Write-Host "Built Meteor Download Manager version $version installer and portable ZIP"
}
