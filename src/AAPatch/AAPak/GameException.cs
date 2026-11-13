namespace AAEmu.Commons.Exceptions;

// Minimal stand-in for the exception type AAPak.cs throws, so the pak library can be
// compiled without the rest of AAEmu.Commons.
public class GameException : Exception
{
    public GameException(string message) : base(message)
    {
    }

    public GameException(string message, Exception innerException) : base(message, innerException)
    {
    }
}
