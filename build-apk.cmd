@echo off
setlocal
dotnet run --file "%~dp0build-apk.cs" -- %*
exit /b %errorlevel%
