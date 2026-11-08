# Windows build modes

Use PowerShell from the repository root.

Fast mode with a trusted Windows x64 Portable release ZIP (does not need CMake, vcpkg, or Visual Studio C++ Build Tools):

    .\windows\build.ps1 -UsePrebuiltNative -PrebuiltNativePath "C:\Downloads\NexonicDownloadManager-Windows-Portable.zip" -SkipInstaller

After a complete native SDK has been cached under .native-cache\sdk\win-x64, you can use:

    .\windows\build.ps1 -UsePrebuiltNative -SkipInstaller

Normal full source build (requires MSVC, CMake, vcpkg, and Windows SDK):

    .\windows\build.ps1

Both modes still need .NET 8 SDK, Python, and the extension packaging requirements. Without Inno Setup 6, use -SkipInstaller to build the Portable ZIP only.

The prebuilt source must contain exactly one nexonic_torrent.dll and accompanying native license notices; metadata is checked for win-x64 when available. ZIP extraction is cached by SHA-256 under .native-cache\prebuilt-win-x64. The DLL hash is displayed, but ABI compatibility must still be tested before shipping.

Outputs: build\win, build\MeteorDownloadManager-<version>-windows-portable.zip and (if enabled) build\MeteorDownloadManager-Setup-<version>.exe.

In `-UsePrebuiltNative` mode the build also extracts `ffmpeg.exe` and `ffprobe.exe` from the trusted Nexonic Portable ZIP, alongside `nexonic_torrent.dll`, and copies both directly next to `MeteorDownloadManager.exe`. An extracted SDK directory without an FFmpeg pair can use the existing `.ffmpeg-cache\windows` fallback; a supplied Portable ZIP missing the pair fails explicitly. In a normal source build, `.ffmpeg-cache\windows` remains required. No fixed `ffmpeg\` subdirectory is used.
