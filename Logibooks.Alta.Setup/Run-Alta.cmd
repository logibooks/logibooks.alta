@echo off
setlocal
set "LOGIBOOKS_ALTA_CONFIG_PATH=%LOCALAPPDATA%\Logibooks\Alta\appsettings.json"
set "Logging__EventLog__LogLevel__Default=None"
"%~dp0Logibooks.Alta.Adapter.exe"
