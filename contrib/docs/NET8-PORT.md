# Porting Tug to .NET 8

Upstream Tug targets `netcoreapp2.0` and has been unmaintained since 2019.
The port on the `net8-port` branch gets it building and running on .NET 8
(current LTS). Summary of the changes and the reasoning.

## Framework / project files

- All projects retargeted `netcoreapp2.0` → `net8.0`.
- The `Microsoft.AspNetCore.All` metapackage (removed after 2.0) → a
  `<FrameworkReference Include="Microsoft.AspNetCore.App" />`.
- MEF: `Microsoft.Composition` → `System.Composition` 8.0.0.
- Added explicit package references that used to come transitively:
  `Newtonsoft.Json`, `Microsoft.Extensions.DependencyModel`,
  `Microsoft.AspNetCore.Mvc.NewtonsoftJson`.

## Startup / hosting

- `app.UseMvc()` (MVC-era routing) → endpoint routing:
  `UseRouting()` + `UseEndpoints(e => e.MapControllers())`.
- `services.AddMvc().AddJsonOptions(...)` → `AddControllers().AddNewtonsoftJson(...)`.
  Tug's models rely on Newtonsoft attributes and, crucially, on Newtonsoft's
  exact serialization (see `VeryStrictInputFilter`), so System.Text.Json is
  not a drop-in here.
- `IHostingEnvironment` → `IWebHostEnvironment`.
- `LoggerFactory` construction modernized to `LoggerFactory.Create(...)`.
- `Assembly.Location` guarded so a single-file publish doesn't throw.

## Two runtime fixes found by testing against a real WMF 5.1 LCM

### 1. Kestrel `AllowSynchronousIO = true`

Tug's authorization filters read the request body synchronously
(`Stream.CopyTo`) to compute the HMAC over the raw bytes. Kestrel disallows
synchronous IO by default since ASP.NET Core 2.1, so registration threw
"Synchronous operations are disallowed". Re-enabled on the Kestrel options.

A cleaner long-term fix is to make those reads async; enabling sync IO keeps
the port minimal and faithful to upstream behavior.

### 2. `DscRegKeyAuthzFilter` — accept a valid signature on any route

The stock filter validates the registration-key HMAC only on the `Register`
route. Every other route is authorized purely by "is this AgentId already
registered?". But a real WMF 5.1 LCM's **first** call after
`Set-DscLocalConfigurationManager` is a `SendReport`, sent *before*
registration finishes. The stock filter returned 401, which the LCM surfaces
as a client crash.

Fix: on non-Register routes, accept the request if the AgentId is already
authorized **or** the request carries a currently-valid registration-key HMAC
signature. Possessing the registration key is already the trust anchor (it's
what lets you register at all), so this doesn't weaken the model — it just
lets the legitimate pre-registration `SendReport` through.

The test harness in `contrib/tools/dsc_server_test.py` asserts the resulting
behavior: forged or absent signatures on an unknown agent are still rejected
with 401.

## Legacy WMF 4.0 (v1) protocol

Beyond the port, this fork adds the older **v1.0/1.1** pull protocol
(`DscV1Controller`) so PowerShell 4.0 nodes work against the same server — the
upstream project only implemented the v2 (WMF 5.x) routes. The details come
straight from **[MS-DSCPM]** sections 3.1/3.3 (verified against a real Windows
Server 2012 R2 / PS 4.0 node):

- **No registration, no HMAC.** Configs are addressed by a `ConfigurationId`
  GUID; possessing it is the token. Deploy the MOF as `<ConfigurationId>.mof`.
- **Status check** — `POST .../Action(ConfigurationId='<guid>')/GetAction`.
  Request body is a flat `{Checksum, ChecksumAlgorithm, NodeCompliant, …}` (not
  the v2 `ClientStatus` array). Response body is **`{"value":"GetConfiguration"}`**
  (or `"OK"`) — a lowercase `value` field. Returning the v2-style `NodeStatus`
  instead makes the v4 client fail with `WebDownloadManagerGetActionUnexpectedResult`.
- **Download** — `GET .../Action(ConfigurationId='<guid>')/ConfigurationContent`
  (note: `Action(…)`, not `Configurations(…)`), with `Checksum` /
  `ChecksumAlgorithm` response headers.
- **Report** — `POST .../Node(s)(ConfigurationId='<guid>')/SendStatusReport`
  (the spec uses both spellings); stored under `Reports/<guid>/`.

The v2 global filters (registration/HMAC, very-strict input) no-op on these
actions because they bind no `DscRequest` model, so v1 stays correctly
unauthenticated.

**Client quirk worth knowing:** the v4 `WebDownloadManager` fails to build its
request URI from a bare-host `ServerUrl` — the meta-config must give it a
**path** (classically `…/PSDSCPullServer.svc`), after which it appends the OData
operations. With that, the v4 downloader traverses Cloudflare fine (edge accepts
TLS 1.0; it sends SNI), so v1 and v2 nodes share one proxied hostname.

> The WMF **5.1** in-box download manager, by contrast, has a long-standing
> client-side bug (`DownloadManagerBase.SendStatusReport` throws a format
> exception before any HTTP request — see PowerShell/PowerShell#2921); it is
> frozen and identical across Server 2016/2019/2022 and is not fixable
> server-side. The v2 server itself is exercised by the harness instead.
