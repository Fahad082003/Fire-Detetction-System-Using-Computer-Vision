using Microsoft.AspNetCore.Mvc;

namespace Sentinel.Api.Middleware;

/// <summary>
/// Converts exceptions thrown anywhere in the pipeline into RFC 7807 ProblemDetails JSON,
/// so the Angular client always receives a predictable error shape.
/// </summary>
public class ExceptionHandlingMiddleware(RequestDelegate next, ILogger<ExceptionHandlingMiddleware> logger, IHostEnvironment env)
{
    public async Task InvokeAsync(HttpContext context)
    {
        try
        {
            await next(context);
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
            // Client disconnected (e.g. closed MJPEG stream) — nothing to report.
        }
        catch (Exception ex)
        {
            if (context.Response.HasStarted)
            {
                logger.LogWarning(ex, "Exception after response started for {Path}", context.Request.Path);
                return;
            }
            await WriteProblemAsync(context, ex);
        }
    }

    private async Task WriteProblemAsync(HttpContext context, Exception ex)
    {
        ProblemDetails problem = ex switch
        {
            AppValidationException v => new ValidationProblemDetails(v.Errors.ToDictionary(k => k.Key, k => k.Value))
            {
                Status = StatusCodes.Status400BadRequest,
                Title = v.Message,
            },
            NotFoundException => new ProblemDetails { Status = StatusCodes.Status404NotFound, Title = "Not found", Detail = ex.Message },
            UnauthorizedAppException => new ProblemDetails { Status = StatusCodes.Status401Unauthorized, Title = "Unauthorized", Detail = ex.Message },
            _ => new ProblemDetails
            {
                Status = StatusCodes.Status500InternalServerError,
                Title = "An unexpected error occurred.",
                Detail = env.IsDevelopment() ? ex.ToString() : null,
            },
        };

        if (problem.Status >= 500)
            logger.LogError(ex, "Unhandled exception for {Method} {Path}", context.Request.Method, context.Request.Path);

        problem.Extensions["traceId"] = context.TraceIdentifier;
        context.Response.StatusCode = problem.Status ?? 500;
        await context.Response.WriteAsJsonAsync(problem, problem.GetType(), options: null, contentType: "application/problem+json");
    }
}
