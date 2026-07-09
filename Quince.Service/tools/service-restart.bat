@echo off
net session >nul 2>&1
if %errorLevel% neq 0 (
    echo Requesting administrative privileges...
    powershell -Command "Start-Process '%~f0' -Verb RunAs"
    exit /b
)

REM sc.exe has no "restart" verb - stop then start explicitly
sc.exe stop QuinceAudioLogger
timeout /t 3 /nobreak >nul
sc.exe start QuinceAudioLogger
pause

