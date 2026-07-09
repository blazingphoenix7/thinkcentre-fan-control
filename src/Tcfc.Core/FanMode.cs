namespace Tcfc.Core;

/// <summary>
/// Firmware fan modes as used by the board's SmartFanMode WMI methods.
/// Values are the raw Data values the firmware accepts/returns.
/// </summary>
public enum FanMode
{
    Quiet = 1,
    Balanced = 2,
    Performance = 3,
}
