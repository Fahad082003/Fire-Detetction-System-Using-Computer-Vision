namespace Sentinel.Api.Middleware;

/// <summary>Thrown by services when a requested record does not exist (→ HTTP 404).</summary>
public class NotFoundException(string message) : Exception(message);

/// <summary>Thrown by services for business-rule / payload validation failures (→ HTTP 400).</summary>
public class AppValidationException : Exception
{
    public IReadOnlyDictionary<string, string[]> Errors { get; }

    public AppValidationException(IDictionary<string, string[]> errors)
        : base("One or more validation errors occurred.")
    {
        Errors = new Dictionary<string, string[]>(errors);
    }

    public AppValidationException(string field, string error)
        : this(new Dictionary<string, string[]> { [field] = [error] }) { }
}

/// <summary>Thrown when credentials are wrong or an account is disabled (→ HTTP 401).</summary>
public class UnauthorizedAppException(string message) : Exception(message);
