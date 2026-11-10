@echo off
rem Runs the local server (HTTPS :443). Keep this window open. No admin needed.
rem --watch restarts the server by itself when server.js or src\news.js change.
cd /d "%~dp0"
node --watch server.js
pause
