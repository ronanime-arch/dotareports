@echo off
chcp 65001 >nul
rem Без аргументов: показать состояние и предложить включить или выключить.
if not "%~1"=="" (
  node "%~dp0imba-stay.js" %*
  exit /b %errorlevel%
)
node "%~dp0imba-stay.js" status
echo.
choice /c 120 /n /m "1 - включить (оставаться на итогах IMBA), 2 - выключить, 0 - выход: "
if errorlevel 3 exit /b 0
if errorlevel 2 (node "%~dp0imba-stay.js" off) else (node "%~dp0imba-stay.js" on)
pause
