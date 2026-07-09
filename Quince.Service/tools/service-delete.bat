@echo off
net session >nul 2>&1
if %errorLevel% neq 0 (
    echo Requesting administrative privileges...
    powershell -Command "Start-Process '%~f0' -Verb RunAs"
    exit /b
)

sc.exe stop QuinceAudioLogger
sc.exe delete QuinceAudioLogger
pause

