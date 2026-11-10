@echo off
rem Double-click: asks for admin (UAC), then installs the hosts entry + local certificate.
powershell -NoProfile -ExecutionPolicy Bypass -Command "Start-Process powershell -Verb RunAs -ArgumentList '-NoProfile','-ExecutionPolicy','Bypass','-File','\"%~dp0scripts\install.ps1\"'"
