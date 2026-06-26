@echo off
chcp 65001 >nul
REM Lanza el asistente interactivo de sincronización con velopack/velopack
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0sync-upstream.ps1" %*
