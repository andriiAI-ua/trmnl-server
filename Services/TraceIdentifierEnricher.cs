namespace Trmnl.Server.Services
{
    using Serilog.Core;
    using Serilog.Events;

    public sealed class TraceIdentifierEnricher : ILogEventEnricher
    {
        private readonly IHttpContextAccessor _httpContextAccessor;

        public TraceIdentifierEnricher(IHttpContextAccessor httpContextAccessor)
        {
            _httpContextAccessor = httpContextAccessor;
        }

        public void Enrich(LogEvent logEvent, ILogEventPropertyFactory propertyFactory)
        {
            var traceId = _httpContextAccessor.HttpContext?.TraceIdentifier;

            if (!string.IsNullOrEmpty(traceId))
            {
                var property = propertyFactory.CreateProperty("CorrelationId", traceId);
                logEvent.AddPropertyIfAbsent(property);
            }
        }
    }
}