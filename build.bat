@echo off
rem Builds the mod and packs it into dist\vsterraindiffusion_<version>.zip
rem
rem Set VINTAGE_STORY to your Vintage Story install if it is not in %APPDATA%\Vintagestory.
rem Usage: build.bat [Configuration]   (default Release)

setlocal

set "root=%~dp0"
if "%root:~-1%"=="\" set "root=%root:~0,-1%"
set "project=%root%\src\VSTerrainDiffusion"

set "configuration=%~1"
if "%configuration%"=="" set "configuration=Release"

set "output=%project%\bin\%configuration%"
set "dist=%root%\dist"
set "staging="

rem The project file finds the game itself on Linux and macOS, but has no default that exists on
rem Windows, so the usual install location is offered here before asking for the variable.
if not defined VINTAGE_STORY (
    if exist "%APPDATA%\Vintagestory\VintagestoryAPI.dll" (
        set "VINTAGE_STORY=%APPDATA%\Vintagestory"
        echo Using Vintage Story at %APPDATA%\Vintagestory
    )
)
if not defined VINTAGE_STORY goto :novintagestory
if not exist "%VINTAGE_STORY%\VintagestoryAPI.dll" goto :novintagestory

for /f "usebackq delims=" %%v in (`powershell -NoProfile -ExecutionPolicy Bypass -Command "(Get-Content -Raw -LiteralPath '%project%\modinfo.json' | ConvertFrom-Json).version"`) do set "version=%%v"
if not defined version (
    echo Could not read the mod version from "%project%\modinfo.json".
    goto :fail
)

set "archive=%dist%\vsterraindiffusion_%version%.zip"

echo Building %configuration%...
dotnet build "%project%" -c %configuration% -v minimal -nologo
if errorlevel 1 goto :fail

echo Packing %archive%
if not exist "%dist%" mkdir "%dist%"
if errorlevel 1 goto :fail

set "staging=%TEMP%\vsterraindiffusion-build-%RANDOM%%RANDOM%"
mkdir "%staging%"
if errorlevel 1 goto :fail

copy /y "%output%\VSTerrainDiffusion.dll" "%staging%\" >nul
if errorlevel 1 goto :fail
copy /y "%output%\Microsoft.ML.OnnxRuntime.dll" "%staging%\" >nul
if errorlevel 1 goto :fail
copy /y "%output%\System.Numerics.Tensors.dll" "%staging%\" >nul
if errorlevel 1 goto :fail
copy /y "%project%\modinfo.json" "%staging%\" >nul
if errorlevel 1 goto :fail
if exist "%project%\modicon.png" (
    copy /y "%project%\modicon.png" "%staging%\" >nul
    if errorlevel 1 goto :fail
)
xcopy "%project%\assets" "%staging%\assets\" /e /i /q /y >nul
if errorlevel 1 goto :fail

rem Compress-Archive writes entry names with backslashes on Windows PowerShell, which the game
rem cannot read assets out of, so the archive is written through the zip API with the separator
rem the format actually specifies.
powershell -NoProfile -ExecutionPolicy Bypass -Command "$ErrorActionPreference = 'Stop'; Add-Type -AssemblyName System.IO.Compression.FileSystem; $s = [System.IO.Path]::GetFullPath('%staging%'); $z = '%archive%'; if (Test-Path -LiteralPath $z) { Remove-Item -LiteralPath $z -Force }; $a = [System.IO.Compression.ZipFile]::Open($z, 'Create'); try { foreach ($f in Get-ChildItem -LiteralPath $s -Recurse -File) { $n = $f.FullName.Substring($s.Length + 1).Replace([char]92, [char]47); [void][System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile($a, $f.FullName, $n) } } finally { $a.Dispose() }"
if errorlevel 1 goto :fail

echo Done: %archive%
powershell -NoProfile -ExecutionPolicy Bypass -Command "$ErrorActionPreference = 'Stop'; Add-Type -AssemblyName System.IO.Compression.FileSystem; $a = [System.IO.Compression.ZipFile]::OpenRead('%archive%'); try { $a.Entries | ForEach-Object { '{0,10}  {1}' -f $_.Length, $_.FullName } } finally { $a.Dispose() }"
if errorlevel 1 goto :fail

call :cleanup
endlocal
exit /b 0

:novintagestory
echo Could not locate the Vintage Story installation.
echo Set the VINTAGE_STORY environment variable to the folder holding VintagestoryAPI.dll, e.g.
echo     set "VINTAGE_STORY=%%APPDATA%%\Vintagestory"
goto :fail

:fail
call :cleanup
echo Build failed.
endlocal
exit /b 1

:cleanup
if defined staging if exist "%staging%" rmdir /s /q "%staging%"
exit /b 0
