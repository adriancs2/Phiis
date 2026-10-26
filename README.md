# Phiis - PHP Configurator for IIS - The New PHP Manager for IIS

A modern, public-domain alternative to the archived *PHP Manager 2 for IIS*, built for PHP 8.x.

Phiis doesn't run PHP. IIS's FastCGI module does that. Phiis configures IIS FastCGI and `php.ini`
so PHP runs correctly on IIS, and tells you clearly when something is wrong.

**For humans and AI agents alike:** every command supports `--json`, every change supports
`--dry-run`, changes are idempotent, and every changed file is backed up first.

## Download

Get the latest `Phiis-<version>-x64.msi` from the [Releases](../../releases/latest) page and run it
(see [Install](#install)).

## Features

- **IIS Manager page** ("Phiis (PHP)" tile, server or site level): health check, extension on/off
  checkboxes, built-in `php.ini` editor, and a **PHP version switcher** that makes any registered
  `php-cgi.exe` the active PHP for a site or the whole server
- **Health check** (`phiis doctor`): FastCGI module, handler mappings, TS/NTS, end-of-life dates,
  `php.ini`, extensions, and the matching **Visual C++ runtime** for PHP 8.x / 7.x (VC14+) down to
  PHP 5.5/5.6 (VC11), 5.3/5.4 (VC9) and 5.2 (VC6)
- **CLI and PowerShell** for scripts, CI and AI agents

## Requirements

- Windows x64 with IIS and the **CGI** role service (FastCGI)
- IIS Management Console, for the IIS Manager page (the CLI works without it)
- .NET Framework 4.8 (built into Windows 10 1903+, Windows 11 and Server 2022+; on Server 2016/2019
  install it first)
- Administrator rights for commands that read or change IIS configuration

## Usage

```
phiis info D:\php\8.4\php-cgi.exe        # inspect a PHP build (no admin needed)
phiis register D:\php\8.4\php-cgi.exe    # register with IIS and make it active (also switches versions)
phiis register D:\php\8.4\php-cgi.exe --site "Default Web Site" --dry-run
phiis status
phiis doctor                             # exit code 2 when errors are found
phiis ini set date.timezone Asia/Kuala_Lumpur
phiis ext enable curl                    # same as php_curl.dll; never duplicates
phiis ext enable opcache --zend
phiis ext list --json
```

Backups: `%ProgramData%\Phiis\backups`

### PowerShell

The MSI installs a `Phiis` module (a wrapper over `phiis.exe --json`; `-WhatIf` = `--dry-run`):

```powershell
Import-Module Phiis
Test-PhiisConfiguration | Where-Object severity -eq 'error'
Register-PhiisPhp D:\php\8.4\php-cgi.exe -WhatIf
Get-PhiisExtension | Where-Object { -not $_.enabled } | Select-Object name
Enable-PhiisExtension intl
Enable-PhiisExtension opcache -Zend
Set-PhiisIniValue date.timezone Asia/Kuala_Lumpur
```

## Design principles

- **No hardcoded PHP facts in code.** Version, architecture, TS/NTS and toolset are read from the
  binary. End-of-life dates come from `php-lifecycle.json`, which can be updated without a rebuild by
  placing a copy in `%ProgramData%\Phiis\php-lifecycle.json`. Unknown (newer) versions are reported as
  "unknown", never as "obsolete".
- **No hidden state.** Everything is read from the real IIS configuration and `php.ini`, so manual edits,
  PHP Manager 2 leftovers and other tools are picked up.
- **Site-level changes go to `applicationHost.config`**, never into a site's `web.config`.
- **.NET Framework 4.8 / C# 7.3**, so it runs on any IIS server without installing a new runtime.

## Project layout

| Project | Purpose |
|---|---|
| `src/Phiis.Core` | All logic: PHP inspection, php.ini editing, IIS configuration, diagnostics |
| `src/Phiis.Cli` | `phiis.exe` command line |
| `src/Phiis.IisManager` | IIS Manager snap-in (the "Phiis (PHP)" tile), local connections only for now |
| `src/Phiis.PowerShell` | `Phiis` PowerShell module |
| `src/Phiis.Tests` | Unit tests (xUnit) |
| `src/Phiis.Setup` | WiX v5 MSI: installs to Program Files, adds `phiis` to PATH, installs the snap-in into the GAC and registers it with IIS Manager |

Planned: remote IIS Manager connections, MCP server for AI agents, installer dialogs, Authenticode signing.

## Install

Download the MSI from [Releases](../../releases/latest), double-click it, then reopen IIS Manager.
Or from an elevated prompt:

```
msiexec /i Phiis-0.3.0-x64.msi
```

Silent install: add `/qn`. Installing a newer MSI upgrades the old one in place. Uninstall removes the IIS Manager registration, but never touches your PHP
or IIS PHP configuration.

`phiis snapin status` lists PHP-related IIS Manager snap-ins, including PHP Manager 2 if it is still
installed. Both work on the same settings, so uninstalling PHP Manager 2 is recommended.

Assemblies are strong-named with `src/Phiis.snk` (required for the GAC). Strong naming is an identity,
not a security feature; Authenticode signing of the MSI is a separate, later step.

## Build

Needs the .NET SDK and the IIS Management Console (the snap-in compiles against
`Microsoft.Web.Management.dll`).

```
dotnet build src/Phiis.slnx -c Release
dotnet test src/Phiis.slnx
```

The MSI is written to `src/Phiis.Setup/bin/x64/Release/`.

## Releasing

Bump `<Version>` in `src/Directory.Build.props` (and `ModuleVersion` in `src/Phiis.PowerShell/Phiis.psd1`),
commit, then push a matching tag:

```
git tag v0.3.0
git push origin v0.3.0
```

The `release` workflow builds, tests and publishes a GitHub Release with the MSI and its SHA-256 checksum.

## License

Public domain ([The Unlicense](LICENSE)). Phiis is an independent project written from scratch. It is
not affiliated with the PHP Manager for IIS project, Microsoft, or The PHP Group.
