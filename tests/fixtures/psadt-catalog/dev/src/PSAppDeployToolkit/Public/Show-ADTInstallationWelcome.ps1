function Show-ADTInstallationWelcome {
    <#
    .SYNOPSIS
        Show a welcome dialog to the user.
    .DESCRIPTION
        Displays a welcome dialog that can close processes and allow deferral.
    .PARAMETER CloseProcesses
        Processes to close before installation.
    .PARAMETER Silent
        Suppresses the dialog.
    .EXAMPLE
        Show-ADTInstallationWelcome -CloseProcesses 'notepad'
    .OUTPUTS
        None
    #>

    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [System.String[]]$CloseProcesses,

        [Parameter(Mandatory = $false)]
        [System.Management.Automation.SwitchParameter]$Silent
    )
}
