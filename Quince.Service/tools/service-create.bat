@echo off
net session >nul 2>&1
if %errorLevel% neq 0 (
    echo Requesting administrative privileges...
    powershell -Command "Start-Process '%~f0' -Verb RunAs"
    exit /b
)

sc.exe create QuinceAudioLogger binPath= "C:\Quince\Quince.Service.exe" start= auto DisplayName= "Quince Audiologger"
pause

