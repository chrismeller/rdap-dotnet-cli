using System.Net;

namespace Rdap;

public class RdapException : Exception
{
    public RdapException() { }
    public RdapException(string message) : base(message) { }
    public RdapException(string message, Exception innerException) : base(message, innerException) { }
}

/// <summary>The response could not be parsed into the RDAP model.</summary>
public sealed class RdapParseException : RdapException
{
    public RdapParseException() { }
    public RdapParseException(string message) : base(message) { }
    public RdapParseException(string message, Exception innerException) : base(message, innerException) { }
}

/// <summary>No RDAP server is known for the query (not covered by the IANA bootstrap registries).</summary>
public sealed class RdapNoServerException : RdapException
{
    public RdapNoServerException() { }
    public RdapNoServerException(string message) : base(message) { }
    public RdapNoServerException(string message, Exception innerException) : base(message, innerException) { }
}

/// <summary>The server answered with a non-success HTTP status.</summary>
public sealed class RdapHttpException : RdapException
{
    public RdapHttpException() { }
    public RdapHttpException(string message) : base(message) { }
    public RdapHttpException(string message, Exception innerException) : base(message, innerException) { }

    public RdapHttpException(string message, HttpStatusCode statusCode, Uri requestUri, TimeSpan? retryAfter = null)
        : base(message)
    {
        StatusCode = statusCode;
        RequestUri = requestUri;
        RetryAfter = retryAfter;
    }

    public HttpStatusCode StatusCode { get; }
    public Uri? RequestUri { get; }
    public TimeSpan? RetryAfter { get; }
}
