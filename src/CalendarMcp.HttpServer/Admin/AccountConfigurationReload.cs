namespace CalendarMcp.HttpServer.Admin;

/// <summary>
/// Account writes go to appsettings.json while every admin read comes from the in-memory
/// <c>AccountRegistry</c>, which reloadOnChange only refreshes when the file watcher fires —
/// measured 250-500 ms after the write had already been answered. A caller that read right
/// after its own write (the cockpit after Disconnect, google/start after create) saw the old
/// accounts. Reloading the configuration explicitly runs the <c>IOptionsMonitor</c> change
/// callbacks synchronously, so the registry is current before the response leaves.
/// </summary>
internal static class AccountConfigurationReload
{
    public static void Apply(IConfiguration configuration)
    {
        if (configuration is IConfigurationRoot root)
        {
            root.Reload();
        }
    }
}
