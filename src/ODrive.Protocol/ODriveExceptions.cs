namespace ODrive.Protocol;

/// <summary>La liaison avec la carte est perdue : débranchement, reboot, ou absence de réponse après tous les essais.</summary>
public class ODriveConnectionLostException : IOException
{
    public ODriveConnectionLostException() : this("La liaison avec la carte ODrive est perdue.") { }
    public ODriveConnectionLostException(string message) : base(message) { }
    public ODriveConnectionLostException(string message, Exception innerException) : base(message, innerException) { }
}

/// <summary>La carte a répondu, mais pas ce que le protocole prévoit.</summary>
public class ODriveProtocolException : Exception
{
    public ODriveProtocolException() : this("Réponse inattendue de la carte ODrive.") { }
    public ODriveProtocolException(string message) : base(message) { }
    public ODriveProtocolException(string message, Exception innerException) : base(message, innerException) { }
}
