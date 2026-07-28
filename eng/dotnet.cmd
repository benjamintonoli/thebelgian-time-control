@echo off
set "DOTNET_CLI_HOME=%~dp0..\.dotnet-cli"
set "NUGET_PACKAGES=%~dp0..\.nuget\packages"
set "DOTNET_NOLOGO=1"
dotnet %*
