namespace Tcfc.Core;

/// <summary>
/// Thrown when the PawnIO driver, its EC port-I/O module, or the embedded
/// controller itself is not usable on this machine (driver not installed,
/// process not elevated, module blob missing, or a PawnIO call failed).
/// </summary>
public sealed class EcUnavailableException : Exception
{
    public EcUnavailableException(string message)
        : base(message)
    {
    }

    public EcUnavailableException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
