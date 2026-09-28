using System.Net;

namespace Quince.Service.Services;

public static class ZabbixEndpoint
{
    public const string Path = "/api/monitoring/zabbix";

    public static bool IsLocalRequest(HttpContext context)
    {
        var ip = context.Connection.RemoteIpAddress;
        if (ip?.IsIPv4MappedToIPv6 == true) ip = ip.MapToIPv4();
        return ip != null && IPAddress.IsLoopback(ip)
            && !context.Request.Headers.ContainsKey("Forwarded")
            && !context.Request.Headers.Keys.Any(k => k.StartsWith("X-Forwarded-", StringComparison.OrdinalIgnoreCase));
    }

    public static async Task HandleAsync(HttpContext context)
    {
        context.Response.Headers.CacheControl = "no-store";
        if (!IsLocalRequest(context))
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return;
        }
        if (!HttpMethods.IsGet(context.Request.Method))
        {
            context.Response.StatusCode = StatusCodes.Status405MethodNotAllowed;
            context.Response.Headers.Allow = "GET";
            return;
        }
        var json = context.RequestServices.GetRequiredService<ZabbixMonitoringService>().Snapshot;
        if (json == null)
        {
            context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            return;
        }
        context.Response.ContentType = "application/json; charset=utf-8";
        await context.Response.WriteAsync(json, context.RequestAborted);
    }
}
