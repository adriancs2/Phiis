@{
    RootModule        = 'Phiis.psm1'
    ModuleVersion     = '0.4.0'
    GUID              = '6f0c8a51-3c2e-4b8e-9d7a-2a5f4e1b7c90'
    Author            = 'Phiis contributors'
    Description       = 'Phiis - PHP Configurator for IIS. PowerShell wrapper over phiis.exe.'
    Copyright         = 'Public domain (The Unlicense)'
    PowerShellVersion = '5.1'
    FunctionsToExport = @(
        'Get-PhiisPhpInfo', 'Get-PhiisStatus', 'Test-PhiisConfiguration', 'Register-PhiisPhp', 'Unregister-PhiisPhp', 'Use-PhiisGlobalPhp',
        'Get-PhiisIniValue', 'Set-PhiisIniValue', 'Get-PhiisExtension', 'Enable-PhiisExtension', 'Disable-PhiisExtension'
    )
    CmdletsToExport   = @()
    VariablesToExport = @()
    AliasesToExport   = @()
    PrivateData       = @{
        PSData = @{
            Tags       = @('PHP', 'IIS', 'FastCGI', 'Windows')
            LicenseUri = 'https://unlicense.org'
        }
    }
}
