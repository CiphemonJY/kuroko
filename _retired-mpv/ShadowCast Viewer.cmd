@echo off
title ShadowCast Viewer
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0viewer.ps1" %*
