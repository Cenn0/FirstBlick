# Explicit HTTPS proxy hosting

DP-02-07 / DP-13-05, software hosting prerequisite. The local Compose stack remains the documented loopback development profile.

For a TLS-terminating proxy, set `DOTNET_ENVIRONMENT=Production`, `ASPNETCORE_ENVIRONMENT=Production`, `App__BaseUrl=https://<chosen-host>`, `AllowedHosts=<chosen-host>` and `Hosting__KnownProxies__0=<actual-proxy-IP>`. Configure the existing RSA Data Protection certificate and its password for API and worker. Set the HTTPS redirection port through `ASPNETCORE_HTTPS_PORT=443` when there is no Kestrel HTTPS listener. Never enable the generic `ASPNETCORE_FORWARDEDHEADERS_ENABLED` switch, which bypasses this explicit trust contract.

The proxy must overwrite `X-Forwarded-Proto` with its actual request scheme and append the actual client peer to `X-Forwarded-For`. FirstBlick consumes only the rightmost hop and requires both headers. It does not accept forwarded host or path prefix. Configure the public Host header and host allowlist separately. The operator owns the integrity of the trusted proxy, its address and its header configuration; authorizing an arbitrary peer as a proxy grants that peer the ability to assert client IP and scheme. A dedicated ingress network keeps database, object storage and worker off the proxy network.

With no explicit entries, forwarded headers remain disabled. Default loopback trust is cleared. Networks, DNS names, wildcard addresses, multicast addresses and more than 16 entries fail startup. Unknown peers cannot change scheme or client address. This is verified through the actual ASP.NET forwarding middleware, including incomplete header pairs, extra client-supplied hops and unchanged host/prefix.

Use an explicitly trusted loopback peer only if the operator deliberately requires local HTTPS-aware health probes. A health probe in that profile supplies a complete forwarded pair from loopback and still verifies database readiness. Public HTTPS checks must additionally verify TLS, secure cookies, antiforgery, login, tenant authorization, static assets and retained originals across restart. A successful internal probe alone is not public acceptance.

`FIRSTBLICK_SMOKE_BASE_URL=https://<chosen-test-host> python3 scripts/container-smoke.py` runs the existing mutating synthetic workflow against an explicitly chosen verified HTTPS origin. It uses the private local `.env` bootstrap account. Run it only against the isolated test installation. TLS verification stays enabled; non-loopback HTTP and origins containing credentials, paths, queries or fragments are rejected. The default remains the local loopback stack.

This implements proxy trust and HTTPS smoke targeting. External provider delivery, secret rotation, migration/cutover and full DP-02-07/DP-13-05 acceptance still require their remaining work and inputs.

Reference: [Microsoft ASP.NET Core proxy hosting](https://learn.microsoft.com/en-us/aspnet/core/host-and-deploy/proxy-load-balancer?view=aspnetcore-10.0).
