function Add-ADTFont {
    <#
    .SYNOPSIS
        Installs fonts from the given paths.
    .DESCRIPTION
        Installs one or more font files.
    .PARAMETER Path
        The font files to install.
    .PARAMETER Recurse
        Searches subfolders.
    .EXAMPLE
        Add-ADTFont -Path '.\Fonts'
    .OUTPUTS
        None
    #>

    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [System.String[]]$Path,

        [Parameter(Mandatory = $false)]
        [System.Management.Automation.SwitchParameter]$Recurse
    )
}
