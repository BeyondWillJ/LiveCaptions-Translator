@echo off
setlocal EnableExtensions

set "ROOT=%~dp0"
set "PROJECT=%ROOT%LiveCaptionsTranslator.csproj"
set "OUTPUT=%ROOT%artifacts\publish\win-x64"
set "RID=win-x64"
set "CACHE_ROOT=%ROOT%.build-cache"

rem Keep all writable .NET, NuGet and temporary data inside this workspace.
set "DOTNET_CLI_HOME=%CACHE_ROOT%\dotnet-home"
set "NUGET_PACKAGES=%CACHE_ROOT%\nuget-packages"
set "NUGET_HTTP_CACHE_PATH=%CACHE_ROOT%\nuget-http"
set "NUGET_PLUGINS_CACHE_PATH=%CACHE_ROOT%\nuget-plugins"
set "NUGET_SCRATCH=%CACHE_ROOT%\nuget-scratch"
set "TEMP=%CACHE_ROOT%\temp"
set "TMP=%CACHE_ROOT%\temp"
set "DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1"
set "DOTNET_CLI_TELEMETRY_OPTOUT=1"
set "DOTNET_NOLOGO=1"

rem Give transient NuGet/network failures a few more chances.
set "NUGET_ENHANCED_MAX_NETWORK_TRY_COUNT=6"
set "NUGET_ENHANCED_NETWORK_RETRY_DELAY_MILLISECONDS=2000"

if not exist "%DOTNET_CLI_HOME%" mkdir "%DOTNET_CLI_HOME%"
if not exist "%NUGET_PACKAGES%" mkdir "%NUGET_PACKAGES%"
if not exist "%NUGET_HTTP_CACHE_PATH%" mkdir "%NUGET_HTTP_CACHE_PATH%"
if not exist "%NUGET_PLUGINS_CACHE_PATH%" mkdir "%NUGET_PLUGINS_CACHE_PATH%"
if not exist "%NUGET_SCRATCH%" mkdir "%NUGET_SCRATCH%"
if not exist "%TEMP%" mkdir "%TEMP%"

echo [1/4] Checking .NET SDK...
where dotnet >nul 2>nul
if errorlevel 1 (
    echo ERROR: dotnet SDK was not found in PATH.
    exit /b 1
)

if not exist "%PROJECT%" (
    echo ERROR: Project file was not found: "%PROJECT%"
    exit /b 1
)

echo [2/4] Restoring packages for %RID% only...
dotnet restore "%PROJECT%" ^
    --runtime %RID% ^
    --disable-parallel ^
    -p:RuntimeIdentifiers=%RID%
if errorlevel 1 (
    echo ERROR: NuGet restore failed.
    echo Check access to https://api.nuget.org/v3/index.json and your HTTP_PROXY/HTTPS_PROXY settings.
    exit /b 1
)

echo [3/4] Cleaning output: "%OUTPUT%"
if exist "%OUTPUT%" rmdir /s /q "%OUTPUT%"
mkdir "%OUTPUT%"
if errorlevel 1 (
    echo ERROR: Could not create the output directory.
    exit /b 1
)

echo [4/4] Publishing %RID% Release build...
dotnet publish "%PROJECT%" ^
    --configuration Release ^
    --runtime %RID% ^
    --self-contained false ^
    --no-restore ^
    -p:RuntimeIdentifiers=%RID% ^
    -p:PublishSingleFile=true ^
    -p:IncludeAllContentForSelfExtract=true ^
    --output "%OUTPUT%"
if errorlevel 1 (
    echo ERROR: dotnet publish failed.
    exit /b 1
)

if not exist "%OUTPUT%\LiveCaptionsTranslator.exe" (
    echo ERROR: Publish completed but the executable was not found.
    exit /b 1
)

echo.
echo Publish succeeded.
echo Output: "%OUTPUT%"
echo.
dir /b "%OUTPUT%"
exit /b 0
