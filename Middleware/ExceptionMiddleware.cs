using System.Text.Json;

public sealed class ExceptionMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<ExceptionMiddleware> _log;

    public ExceptionMiddleware(
        RequestDelegate next,
        ILogger<ExceptionMiddleware> log)
    {
        _next = next;
        _log = log;
    }

    public async Task Invoke(HttpContext ctx)
    {
        try
        {
            await _next(ctx);
        }
        catch (Exception ex)
        {
            _log.LogError(
                ex,
                "Unhandled exception {Method} {Path}",
                ctx.Request.Method,
                ctx.Request.Path);

            ctx.Response.StatusCode = 500;
            ctx.Response.ContentType = "application/json";

            var payload = new
            {
                status = 500,
                error = "internal_error",
                message = ex.Message
            };

            await ctx.Response.WriteAsync(
                JsonSerializer.Serialize(payload));
        }
    }
}
