@echo off
rem Runs the local server (HTTPS :443). Keep this window open. No admin needed.
cd /d "%~dp0"
node server.js
pause
