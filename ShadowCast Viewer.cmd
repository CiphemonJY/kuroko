@echo off
title ShadowCast (Fast)
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0mf-viewer.ps1" %*
