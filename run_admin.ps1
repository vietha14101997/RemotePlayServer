$exe = ".\bin\Debug\net9.0-windows10.0.26100.0\RemotePlayServer.exe"
if (!(Test-Path $exe)) {
    Write-Host "Executable not found at $exe. Please build first." -ForegroundColor Red
    exit
}
Start-Process $exe -Verb RunAs
