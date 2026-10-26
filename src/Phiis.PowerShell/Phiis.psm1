# Phiis PowerShell module - a thin wrapper over 'phiis.exe --json'.
# All logic lives in phiis.exe, so PowerShell, the CLI and IIS Manager always behave the same.
# -WhatIf on any changing command maps to 'phiis --dry-run'.

function Get-PhiisExecutable {
    $candidates = @(
        (Join-Path $env:ProgramFiles 'Phiis\phiis.exe'),
        (Join-Path $PSScriptRoot 'phiis.exe')
    )
    foreach ($candidate in $candidates) {
        if (Test-Path -LiteralPath $candidate) { return $candidate }
    }
    $command = Get-Command phiis.exe -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($command) { return $command.Source }
    throw 'phiis.exe was not found. Install Phiis or add it to PATH.'
}

function Invoke-Phiis {
    param(
        [Parameter(Mandatory)] [string[]] $Arguments,
        [string] $Site,
        [string] $PhpPath,
        [string] $IniPath,
        [switch] $DryRun
    )

    $argList = @($Arguments) + '--json'
    if ($Site)    { $argList += @('--site', $Site) }
    if ($PhpPath) { $argList += @('--php', $PhpPath) }
    if ($IniPath) { $argList += @('--ini', $IniPath) }
    if ($DryRun)  { $argList += '--dry-run' }

    $exe = Get-PhiisExecutable
    $output = & $exe @argList
    $exitCode = $LASTEXITCODE
    $result = ($output -join "`n") | ConvertFrom-Json

    # Exit code 2 = doctor found problems; that is a result, not a failure.
    if ($exitCode -ne 0 -and $exitCode -ne 2) {
        $message = if ($result.error) { $result.error } else { "phiis.exe failed with exit code $exitCode" }
        throw $message
    }
    return $result
}

function Get-PhiisPhpInfo {
    <#
    .SYNOPSIS
    Shows version, architecture, TS/NTS, build toolset and end-of-life of a PHP build.
    .EXAMPLE
    Get-PhiisPhpInfo -Path D:\php\8.4\php-cgi.exe
    #>
    [CmdletBinding()]
    param([Parameter(Mandatory, Position = 0)] [string] $Path)
    Invoke-Phiis -Arguments @('info', $Path)
}

function Get-PhiisStatus {
    <#
    .SYNOPSIS
    Shows PHP handlers, FastCGI applications and php.ini for the server or a site.
    #>
    [CmdletBinding()]
    param([string] $Site)
    Invoke-Phiis -Arguments @('status') -Site $Site
}

function Test-PhiisConfiguration {
    <#
    .SYNOPSIS
    Runs the Phiis health check and returns one object per finding.
    .EXAMPLE
    Test-PhiisConfiguration | Where-Object severity -eq 'error'
    #>
    [CmdletBinding()]
    param([string] $Site)
    (Invoke-Phiis -Arguments @('doctor') -Site $Site).findings
}

function Register-PhiisPhp {
    <#
    .SYNOPSIS
    Registers a php-cgi.exe with IIS and makes it the active PHP. Idempotent.
    .EXAMPLE
    Register-PhiisPhp -Path D:\php\8.4\php-cgi.exe -Site 'Default Web Site' -WhatIf
    #>
    [CmdletBinding(SupportsShouldProcess)]
    param(
        [Parameter(Mandatory, Position = 0)] [string] $Path,
        [string] $Site
    )
    $scope = if ($Site) { "site '$Site'" } else { 'server level' }
    if ($WhatIfPreference) {
        return Invoke-Phiis -Arguments @('register', $Path) -Site $Site -DryRun
    }
    if ($PSCmdlet.ShouldProcess($scope, "Register PHP $Path")) {
        Invoke-Phiis -Arguments @('register', $Path) -Site $Site
    }
}

function Get-PhiisIniValue {
    <#
    .SYNOPSIS
    Reads a php.ini directive of the active PHP (or of -PhpPath / -IniPath).
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory, Position = 0)] [string] $Name,
        [string] $Site, [string] $PhpPath, [string] $IniPath
    )
    Invoke-Phiis -Arguments @('ini', 'get', $Name) -Site $Site -PhpPath $PhpPath -IniPath $IniPath
}

function Set-PhiisIniValue {
    <#
    .SYNOPSIS
    Sets a php.ini directive. The previous php.ini is backed up first.
    .EXAMPLE
    Set-PhiisIniValue date.timezone Asia/Kuala_Lumpur
    #>
    [CmdletBinding(SupportsShouldProcess)]
    param(
        [Parameter(Mandatory, Position = 0)] [string] $Name,
        [Parameter(Mandatory, Position = 1)] [string] $Value,
        [string] $Site, [string] $PhpPath, [string] $IniPath
    )
    if ($WhatIfPreference) {
        return Invoke-Phiis -Arguments @('ini', 'set', $Name, $Value) -Site $Site -PhpPath $PhpPath -IniPath $IniPath -DryRun
    }
    if ($PSCmdlet.ShouldProcess('php.ini', "Set $Name = $Value")) {
        Invoke-Phiis -Arguments @('ini', 'set', $Name, $Value) -Site $Site -PhpPath $PhpPath -IniPath $IniPath
    }
}

function Get-PhiisExtension {
    <#
    .SYNOPSIS
    Lists PHP extensions from php.ini and the ext folder.
    .EXAMPLE
    Get-PhiisExtension | Where-Object enabled
    #>
    [CmdletBinding()]
    param([string] $Site, [string] $PhpPath, [string] $IniPath)
    (Invoke-Phiis -Arguments @('ext', 'list') -Site $Site -PhpPath $PhpPath -IniPath $IniPath).extensions
}

function Enable-PhiisExtension {
    <#
    .SYNOPSIS
    Enables a PHP extension. 'curl', 'php_curl' and 'php_curl.dll' are the same; never duplicates.
    #>
    [CmdletBinding(SupportsShouldProcess)]
    param(
        [Parameter(Mandatory, Position = 0, ValueFromPipelineByPropertyName)] [string] $Name,
        [switch] $Zend,
        [string] $Site, [string] $PhpPath, [string] $IniPath
    )
    process {
        $arguments = @('ext', 'enable', $Name)
        if ($Zend) { $arguments += '--zend' }
        if ($WhatIfPreference) {
            Invoke-Phiis -Arguments $arguments -Site $Site -PhpPath $PhpPath -IniPath $IniPath -DryRun
        }
        elseif ($PSCmdlet.ShouldProcess('php.ini', "Enable extension $Name")) {
            Invoke-Phiis -Arguments $arguments -Site $Site -PhpPath $PhpPath -IniPath $IniPath
        }
    }
}

function Disable-PhiisExtension {
    <#
    .SYNOPSIS
    Disables a PHP extension (comments out every line loading it).
    #>
    [CmdletBinding(SupportsShouldProcess)]
    param(
        [Parameter(Mandatory, Position = 0, ValueFromPipelineByPropertyName)] [string] $Name,
        [string] $Site, [string] $PhpPath, [string] $IniPath
    )
    process {
        if ($WhatIfPreference) {
            Invoke-Phiis -Arguments @('ext', 'disable', $Name) -Site $Site -PhpPath $PhpPath -IniPath $IniPath -DryRun
        }
        elseif ($PSCmdlet.ShouldProcess('php.ini', "Disable extension $Name")) {
            Invoke-Phiis -Arguments @('ext', 'disable', $Name) -Site $Site -PhpPath $PhpPath -IniPath $IniPath
        }
    }
}

Export-ModuleMember -Function Get-PhiisPhpInfo, Get-PhiisStatus, Test-PhiisConfiguration, Register-PhiisPhp,
    Get-PhiisIniValue, Set-PhiisIniValue, Get-PhiisExtension, Enable-PhiisExtension, Disable-PhiisExtension
