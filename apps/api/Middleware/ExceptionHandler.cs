using Compute.Api.Modules.Reservations;
using Microsoft.AspNetCore.Mvc;

namespace Compute.Api.Middleware;

public class ExceptionHandler(RequestDelegate next, ILogger<ExceptionHandler> logger)
{
    public async Task InvokeAsync(HttpContext context)
    {
        try
        {
            await next(context);
        }
        catch (Exception ex)
        {
            if (context.Response.HasStarted)
                throw;

            var (status, title) = ex switch
            {
                DomainException d => (d.Status, d.Message),
                _ => (StatusCodes.Status500InternalServerError, "An error occurred while processing your request."),
            };
            if (status >= 500)
                logger.LogError(ex, "Unhandled exception processing {Method} {Path}", context.Request.Method, context.Request.Path);

            context.Response.StatusCode = status;
            context.Response.ContentType = "application/problem+json";
            await context.Response.WriteAsJsonAsync(new ProblemDetails { Status = status, Title = title });
        }
    }
}
