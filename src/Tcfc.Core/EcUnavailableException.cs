namespace Tcfc.Core;

/// <summary>PawnIO or the EC is not usable here: driver not installed, not elevated, module blob missing, or a call failed.</summary>
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
