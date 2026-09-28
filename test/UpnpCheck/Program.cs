using BALLxPITOnlineCoop.Core;
Log.Sink = (level, message) => Console.WriteLine($"LOG {level} {message}");
var upnp = new UpnpPortMapper();
bool ok = await upnp.MapAsync(47777, CancellationToken.None);
Console.WriteLine($"mapped={ok} external={upnp.ExternalAddress} status={upnp.Status}");
await upnp.UnmapAsync();
return ok && upnp.ExternalAddress == "203.0.113.7" ? 0 : 1;
