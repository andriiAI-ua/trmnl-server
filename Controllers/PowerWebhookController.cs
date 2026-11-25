using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.ModelBinding;

/// <summary>
/// Gets the power status of the building. healthchecks.io sends its notification to a webhook relay,
/// and the relay sends the status here: {"status": "up"} or {"status": "down"}.
/// Only a local process can send it. nginx does not publish this path.
/// </summary>
[ApiController]
[ApiExplorerSettings(IgnoreApi = true)]
[Route("api/power/status")]
[DisableFormValueModelBinding]
public class PowerWebhookController : ControllerBase
{
    private readonly PowerStatusStore _store;
    private readonly ILogger<PowerWebhookController> _log;

    public PowerWebhookController(PowerStatusStore store, ILogger<PowerWebhookController> log)
    {
        _store = store;
        _log = log;
    }

    [HttpPost]
    public async Task<IActionResult> Post(CancellationToken cancellationToken)
    {
        // UseForwardedHeaders trusts only a local proxy, so a client in the network cannot look local.
        var remote = HttpContext.Connection.RemoteIpAddress;
        if (remote is null || !IPAddress.IsLoopback(remote))
        {
            _log.LogWarning("Power status from {Ip} is refused: only a local process can send it", remote);
            return StatusCode(StatusCodes.Status403Forbidden);
        }

        using var reader = new StreamReader(Request.Body);
        var body = await reader.ReadToEndAsync(cancellationToken);

        switch (ReadStatus(body))
        {
            case "up":
                _store.Set(on: true);
                return Ok();
            case "down":
                _store.Set(on: false);
                return Ok();
            default:
                _log.LogWarning("Power status has no \"up\" or \"down\": {Body}", body);
                return BadRequest();
        }
    }

    // Read "status" from a JSON body. Accept a body with only the word too.
    private static string? ReadStatus(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("status", out var s)
                && s.ValueKind == JsonValueKind.String)
            {
                return s.GetString()?.Trim().ToLowerInvariant();
            }
        }
        catch (JsonException)
        {
            return body.Trim().ToLowerInvariant();
        }

        return null;
    }
}

/// <summary>
/// Stop the model binding from reading a form body. Then the action can read the raw body,
/// also when the sender uses the form content type (curl --data does this).
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method)]
public sealed class DisableFormValueModelBindingAttribute : Attribute, IResourceFilter
{
    public void OnResourceExecuting(ResourceExecutingContext context)
    {
        var factories = context.ValueProviderFactories;
        factories.RemoveType<FormValueProviderFactory>();
        factories.RemoveType<FormFileValueProviderFactory>();
        factories.RemoveType<JQueryFormValueProviderFactory>();
    }

    public void OnResourceExecuted(ResourceExecutedContext context)
    {
    }
}
