@echo off
rem Publish TouchpadViewer to D:\Tools\TouchpadGestures and restart it in the tray.

rem Stop the running instance (otherwise the exe is locked)
taskkill /im TouchpadViewer.exe /f >nul 2>&1

dotnet publish "E:\KT_Tools\TouchpadGestures\TouchpadViewer" -c Release -o D:\Tools\TouchpadGestures
if errorlevel 1 (
    echo.
    echo Publish FAILED.
    pause
    exit /b 1
)

start "" "D:\Tools\TouchpadGestures\TouchpadViewer.exe" --tray
echo.
echo Done.
timeout /t 3 >nul