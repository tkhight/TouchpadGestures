@echo off
rem Publish TouchpadViewer and restart it in the tray.
rem Usage: build_touchpadviewer.bat [output_dir]   (default: %%LOCALAPPDATA%%\Programs\TouchpadGestures)
rem The source folder is resolved from this bat's own location, so it can be run from anywhere.

set "OUT=%~1"
if "%OUT%"=="" set "OUT=%LOCALAPPDATA%\Programs\TouchpadGestures"

rem Stop the running instance (otherwise the exe is locked)
taskkill /im TouchpadViewer.exe /f >nul 2>&1

dotnet publish "%~dp0TouchpadViewer" -c Release -o "%OUT%"
if errorlevel 1 (
    echo.
    echo Publish FAILED.
    pause
    exit /b 1
)

start "" "%OUT%\TouchpadViewer.exe" --tray
echo.
echo Done.
timeout /t 3 >nul
