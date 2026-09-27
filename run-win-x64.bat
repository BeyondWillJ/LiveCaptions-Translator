@echo off
setlocal

set "ROOT=%~dp0"
set "APP=%ROOT%artifacts\publish\win-x64\LiveCaptionsTranslator.exe"

if not exist "%APP%" (
    echo ERROR: Published application was not found: "%APP%"
    echo Run publish-win-x64.bat first.
    exit /b 1
)

pushd "%ROOT%" >nul
if errorlevel 1 (
    echo ERROR: Could not enter the application directory: "%ROOT%"
    exit /b 1
)

"%APP%"
set "APP_EXIT=%ERRORLEVEL%"

popd
exit /b %APP_EXIT%
