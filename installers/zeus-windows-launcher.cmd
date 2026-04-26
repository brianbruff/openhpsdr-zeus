@echo off
:: Zeus launcher — starts Zeus.Server.exe in the foreground (so closing
:: this window stops the server) and asynchronously opens the default
:: browser at http://localhost:6060 once the listener is up.
title Zeus Server
cd /d "%~dp0"
echo.
echo   Zeus is starting on http://localhost:6060
echo   Your browser will open as soon as the server is ready.
echo   Close this window to stop Zeus.
echo.
start "" /B powershell -NoProfile -WindowStyle Hidden -Command "for($i=0;$i -lt 60;$i++){try{$t=New-Object Net.Sockets.TcpClient;$t.Connect('127.0.0.1',6060);$t.Close();break}catch{Start-Sleep -Milliseconds 500}};Start-Process 'http://localhost:6060'"
"%~dp0Zeus.Server.exe"
