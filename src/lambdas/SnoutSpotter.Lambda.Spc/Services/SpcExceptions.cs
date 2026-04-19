namespace SnoutSpotter.Lambda.Spc.Services;

public class SpcUnauthorizedException : Exception
{
    public SpcUnauthorizedException(string message) : base(message) { }
}

public class SpcUpstreamException : Exception
{
    public SpcUpstreamException(string message) : base(message) { }
    public SpcUpstreamException(string message, Exception inner) : base(message, inner) { }
}
