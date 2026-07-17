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
