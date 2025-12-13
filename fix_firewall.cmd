@echo off
set "EXE_PATH=%~dp0bin\Debug\net9.0-windows10.0.26100.0\RemotePlayServer.exe"
echo Removing old rules...
netsh advfirewall firewall delete rule name="RemotePlayServer"
echo Adding new rule for: "%EXE_PATH%"
netsh advfirewall firewall add rule name="RemotePlayServer" dir=in action=allow program="%EXE_PATH%" enable=yes profile=any
if %errorlevel% neq 0 (
    echo FAILED. Run as Administrator!
    pause
    exit /b
)
echo SUCCESS. Firewall rule updated.
pause
