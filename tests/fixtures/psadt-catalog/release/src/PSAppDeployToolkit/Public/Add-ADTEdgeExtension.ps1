function Add-ADTEdgeExtension {
    <#
    .SYNOPSIS
        Adds an extension for Microsoft Edge using the ExtensionSettings policy.
    .DESCRIPTION
        Adds an extension for Microsoft Edge using the ExtensionSettings policy.
    .PARAMETER ExtensionID
        The ID of the extension to add.
    .PARAMETER UpdateUrl
        The update URL of the extension.
    .PARAMETER InstallationMode
        How the extension is installed.
    .PARAMETER MinimumVersionRequired
        The minimum extension version.
    .EXAMPLE
        Add-ADTEdgeExtension -ExtensionID 'abc' -UpdateUrl 'https://example.test' -InstallationMode 'normal_installed'
    .OUTPUTS
        None
    #>

    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [System.String]$ExtensionID,

        [Parameter(Mandatory = $true)]
        [System.String]$UpdateUrl,

        [Parameter(Mandatory = $true)]
        [System.String]$InstallationMode,

        [Parameter(Mandatory = $false)]
        [System.String]$MinimumVersionRequired
    )
}
