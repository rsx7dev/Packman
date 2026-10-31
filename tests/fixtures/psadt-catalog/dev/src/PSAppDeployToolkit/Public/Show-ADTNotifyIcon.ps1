function Show-ADTNotifyIcon {
    <#
    .SYNOPSIS
        Shows a notification icon in the system tray.
    .DESCRIPTION
        Shows a notification icon in the system tray.
    .PARAMETER Force
        Replaces an existing icon.
    .EXAMPLE
        Show-ADTNotifyIcon
    .OUTPUTS
        None
    #>

    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $false)]
        [System.Management.Automation.SwitchParameter]$Force
    )
}
