# Luke's Migration Tool

## Description

Migrate virtual machines in a vSphere environment. 

## Requirements

1. .NET Framework 4.8
2. The VMware.PowerCLI PowerShell module is required, because this project references VMware.Vim.dll.

### VMware.PowerCLI Installation

Install using this _exact_ command or you will have to manually fix the Vmware.Vim reference for __MToolVapiClient__. Can be run from PowerShell Core or Windows PowerShell.

`Install-Module VMware.PowerCLI -RequiredVersion 11.3.0.13990089 -Scope CurrentUser -Force`


