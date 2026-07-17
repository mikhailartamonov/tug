// PowerShell.org Tug DSC Pull Server
// Copyright (c) The DevOps Collective, Inc.  All rights reserved.
// Licensed under the MIT license.  See the LICENSE file in the project root for more information.

using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json.Converters;
// using NLog.Extensions.Logging;
// using NLog.Web;
using TugDSC.Server.Configuration;
using TugDSC.Server.Filters;
using TugDSC.Server.Util;

namespace TugDSC.Server.WebAppHost
{
    public class Startup
    {
        #region -- Constants --

        /// <summary>
        /// File name of a required JSON file used to configure the server app.
        /// </summary>
        public const string APP_CONFIG_FILENAME = "appsettings.json";

        /// Defines an optional CLI parameter that can be used to override the
        /// default configuration file.  If specified, the path to the config
        /// file should be specified immediately after (i.e. no space) and
        /// although not strictly enforced, should be fully qualified with
        /// complete path.
        public const string APP_CONFIG_CLI_OVERRIDE = "--config=";

        /// <summary>
        /// File name of an optional JSON file used to override server app configuration.
        /// </summary>
        public const string APP_USER_CONFIG_FILENAME = "appsettings.user.json";
        /// Defines an optional CLI parameter that can be used to override the
        /// default user configuration file.  If specified, the path to the config
        /// file should be specified immediately after (i.e. no space) and
        /// although not strictly enforced, should be fully qualified with
        /// complete path.
        public const string APP_USER_CONFIG_CLI_OVERRIDE = "--userconfig=";

        /// <summary>
        /// Prefix used to identify environment variables that can override server app
        /// configuration.
        /// </summary>
        public const string APP_CONFIG_ENV_PREFIX = "TUG_CFG_";

        public const string APP_CONFIG_CLI_PREFIX = "/c:";

        // Fluent-styled status page served at the site root ("/").
        private const string WelcomePage =
""""
<!doctype html>
<html lang="en">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<title>TugDSC — Pull Server</title>
<style>
  :root {
    --font-ui: "Segoe UI Variable Text", "Segoe UI Variable", "Segoe UI", system-ui, -apple-system, sans-serif;
    --font-display: "Segoe UI Variable Display", "Segoe UI Variable", "Segoe UI", system-ui, sans-serif;
    --font-mono: "Cascadia Code", "Cascadia Mono", Consolas, ui-monospace, monospace;

    /* Mica ground + layered acrylic surfaces (light) */
    --bg-0: #eaeef3;
    --bg-1: #f4f6f9;
    --glow: rgba(0, 103, 192, 0.18);
    --surface: rgba(255, 255, 255, 0.72);
    --surface-hover: rgba(255, 255, 255, 0.9);
    --stroke: rgba(0, 0, 0, 0.07);
    --stroke-strong: rgba(0, 0, 0, 0.12);
    --shadow: 0 2px 4px rgba(0, 0, 0, 0.04), 0 8px 24px rgba(0, 0, 0, 0.07);

    --text-1: rgba(0, 0, 0, 0.89);
    --text-2: rgba(0, 0, 0, 0.58);
    --text-3: rgba(0, 0, 0, 0.42);

    --accent: #005fb8;
    --accent-2: #0078d4;
    --accent-ink: #ffffff;
    --success: #0f7b0f;
    --success-ring: rgba(15, 123, 15, 0.16);
  }

  @media (prefers-color-scheme: dark) {
    :root {
      --bg-0: #1b1b1d;
      --bg-1: #27272b;
      --glow: rgba(96, 205, 255, 0.14);
      --surface: rgba(255, 255, 255, 0.045);
      --surface-hover: rgba(255, 255, 255, 0.075);
      --stroke: rgba(255, 255, 255, 0.08);
      --stroke-strong: rgba(255, 255, 255, 0.14);
      --shadow: 0 2px 4px rgba(0, 0, 0, 0.24), 0 12px 32px rgba(0, 0, 0, 0.36);

      --text-1: rgba(255, 255, 255, 0.92);
      --text-2: rgba(255, 255, 255, 0.62);
      --text-3: rgba(255, 255, 255, 0.44);

      --accent: #60cdff;
      --accent-2: #4cc2ff;
      --accent-ink: #05233a;
      --success: #6ccb5f;
      --success-ring: rgba(108, 203, 95, 0.2);
    }
  }

  /* Explicit toggle overrides (robust in either host) */
  :root[data-theme="light"] {
    --bg-0: #eaeef3; --bg-1: #f4f6f9; --glow: rgba(0,103,192,.18);
    --surface: rgba(255,255,255,.72); --surface-hover: rgba(255,255,255,.9);
    --stroke: rgba(0,0,0,.07); --stroke-strong: rgba(0,0,0,.12);
    --shadow: 0 2px 4px rgba(0,0,0,.04), 0 8px 24px rgba(0,0,0,.07);
    --text-1: rgba(0,0,0,.89); --text-2: rgba(0,0,0,.58); --text-3: rgba(0,0,0,.42);
    --accent: #005fb8; --accent-2: #0078d4; --accent-ink: #fff;
    --success: #0f7b0f; --success-ring: rgba(15,123,15,.16);
  }
  :root[data-theme="dark"] {
    --bg-0: #1b1b1d; --bg-1: #27272b; --glow: rgba(96,205,255,.14);
    --surface: rgba(255,255,255,.045); --surface-hover: rgba(255,255,255,.075);
    --stroke: rgba(255,255,255,.08); --stroke-strong: rgba(255,255,255,.14);
    --shadow: 0 2px 4px rgba(0,0,0,.24), 0 12px 32px rgba(0,0,0,.36);
    --text-1: rgba(255,255,255,.92); --text-2: rgba(255,255,255,.62); --text-3: rgba(255,255,255,.44);
    --accent: #60cdff; --accent-2: #4cc2ff; --accent-ink: #05233a;
    --success: #6ccb5f; --success-ring: rgba(108,203,95,.2);
  }

  * { box-sizing: border-box; }
  html, body { height: 100%; }

  body {
    margin: 0;
    font-family: var(--font-ui);
    color: var(--text-1);
    background:
      radial-gradient(120% 80% at 85% -10%, var(--glow), transparent 55%),
      radial-gradient(90% 70% at 0% 0%, rgba(120,140,170,0.10), transparent 50%),
      linear-gradient(160deg, var(--bg-1), var(--bg-0));
    background-attachment: fixed;
    -webkit-font-smoothing: antialiased;
    text-rendering: optimizeLegibility;
    display: flex;
    justify-content: center;
    align-items: center;
    padding: 48px 24px;
  }

  main { width: 100%; max-width: 720px; }

  /* ---- Header lockup ---- */
  .head { display: flex; align-items: center; gap: 16px; margin-bottom: 26px; }
  .tile {
    flex: none; width: 56px; height: 56px; border-radius: 14px;
    background: linear-gradient(135deg, var(--accent-2), var(--accent));
    display: grid; place-items: center;
    box-shadow: inset 0 1px 0 rgba(255,255,255,0.35), 0 4px 14px rgba(0,60,130,0.28);
  }
  .tile svg { width: 30px; height: 30px; display: block; }
  .titles { display: flex; flex-direction: column; gap: 3px; min-width: 0; }
  .eyebrow {
    font-size: 11px; font-weight: 600; letter-spacing: 0.09em;
    text-transform: uppercase; color: var(--text-3);
  }
  h1 {
    font-family: var(--font-display); font-weight: 650; font-size: 30px;
    line-height: 1.1; margin: 0; letter-spacing: -0.01em; text-wrap: balance;
  }

  /* ---- Status pill ---- */
  .status {
    display: inline-flex; align-items: center; gap: 8px;
    padding: 5px 12px 5px 10px; border-radius: 999px;
    background: var(--surface); border: 1px solid var(--stroke);
    font-size: 13px; font-weight: 600; color: var(--text-1); margin-bottom: 22px;
    backdrop-filter: blur(20px) saturate(140%);
    -webkit-backdrop-filter: blur(20px) saturate(140%);
  }
  .dot { width: 9px; height: 9px; border-radius: 50%; background: var(--success); box-shadow: 0 0 0 4px var(--success-ring); }
  @media (prefers-reduced-motion: no-preference) {
    .dot { animation: pulse 2.6s ease-in-out infinite; }
    @keyframes pulse { 0%,100% { box-shadow: 0 0 0 3px var(--success-ring); } 50% { box-shadow: 0 0 0 6px transparent; } }
  }

  .lede { font-size: 15.5px; line-height: 1.62; color: var(--text-2); margin: 0 0 14px; max-width: 64ch; }
  .lede strong { color: var(--text-1); font-weight: 600; }
  .lede + .lede { margin-bottom: 30px; }

  /* ---- Lifecycle flow ---- */
  .flow {
    list-style: none; margin: 0 0 30px; padding: 0;
    display: grid; grid-template-columns: repeat(4, 1fr); gap: 8px;
    counter-reset: step;
  }
  .flow li { position: relative; padding-top: 36px; }
  .flow li::before {
    counter-increment: step; content: counter(step);
    position: absolute; top: 0; left: 0;
    width: 26px; height: 26px; border-radius: 50%;
    display: grid; place-items: center;
    font-family: var(--font-mono); font-size: 12px; font-weight: 600;
    color: var(--accent); background: var(--surface);
    border: 1px solid var(--stroke-strong);
  }
  .flow li::after {
    content: ""; position: absolute; top: 12.5px; left: 30px; right: -4px;
    height: 2px; border-radius: 2px;
    background: linear-gradient(90deg, var(--stroke-strong), var(--stroke));
  }
  .flow li:last-child::after { display: none; }
  .flow b { display: block; font-family: var(--font-display); font-size: 13.5px; font-weight: 600; color: var(--text-1); }
  .flow span { font-size: 12px; color: var(--text-2); line-height: 1.4; }

  /* ---- Capability cards ---- */
  .cards {
    display: grid; grid-template-columns: repeat(auto-fit, minmax(200px, 1fr));
    gap: 12px; margin-bottom: 28px;
  }
  .card {
    position: relative; padding: 18px; border-radius: 10px;
    background: var(--surface); border: 1px solid var(--stroke); box-shadow: var(--shadow);
    backdrop-filter: blur(30px) saturate(140%);
    -webkit-backdrop-filter: blur(30px) saturate(140%);
    transition: transform 160ms cubic-bezier(.16,1,.3,1), border-color 160ms ease, background 160ms ease;
  }
  .card:hover { background: var(--surface-hover); border-color: var(--stroke-strong); transform: translateY(-2px); }
  .card .ico { width: 26px; height: 26px; margin-bottom: 12px; color: var(--accent); }
  .card .ico svg { width: 100%; height: 100%; display: block; }
  .card h2 { font-family: var(--font-display); font-size: 15px; font-weight: 620; margin: 0 0 5px; color: var(--text-1); }
  .card p { font-size: 13px; line-height: 1.5; color: var(--text-2); margin: 0; }
  .card .meta {
    display: block; margin-top: 10px; font-family: var(--font-mono);
    font-size: 11px; color: var(--text-3); letter-spacing: 0.01em;
  }

  /* ---- Runtime chips ---- */
  .runtime {
    display: flex; flex-wrap: wrap; gap: 8px; align-items: center;
    padding-top: 22px; border-top: 1px solid var(--stroke);
  }
  .runtime .label {
    font-size: 11px; font-weight: 600; letter-spacing: 0.08em; text-transform: uppercase;
    color: var(--text-3); margin-right: 2px;
  }
  .chip {
    font-family: var(--font-mono); font-size: 11.5px; letter-spacing: 0.01em; color: var(--text-2);
    padding: 4px 10px; border-radius: 6px; background: var(--surface); border: 1px solid var(--stroke);
  }
  .chip b { color: var(--accent); font-weight: 600; }

  footer { margin-top: 24px; font-size: 12px; color: var(--text-3); letter-spacing: 0.02em; }

  @media (prefers-reduced-motion: no-preference) {
    .reveal { opacity: 0; transform: translateY(10px); animation: rise .6s cubic-bezier(.16,1,.3,1) forwards; }
    @keyframes rise { to { opacity: 1; transform: none; } }
    .d1 { animation-delay: .05s; } .d2 { animation-delay: .12s; }
    .d3 { animation-delay: .19s; } .d4 { animation-delay: .26s; }
    .d5 { animation-delay: .33s; } .d6 { animation-delay: .40s; }
    .d7 { animation-delay: .47s; }
  }

  @media (max-width: 520px) {
    .flow { grid-template-columns: repeat(2, 1fr); row-gap: 18px; }
    .flow li:nth-child(2)::after { display: none; }
  }
  @media (max-width: 460px) {
    h1 { font-size: 26px; }
    .head { gap: 13px; }
    .tile { width: 50px; height: 50px; border-radius: 12px; }
  }
</style>
</head>
<body>
<main>
  <div class="head reveal d1">
    <div class="tile" aria-hidden="true">
      <svg viewBox="0 0 24 24" fill="none" stroke="#fff" stroke-width="1.7" stroke-linejoin="round" stroke-linecap="round">
        <path d="M12 3 3 7.5l9 4.5 9-4.5L12 3Z" fill="rgba(255,255,255,.22)"/>
        <path d="M3 12.2 12 16.7l9-4.5"/>
        <path d="M3 16.7 12 21.2l9-4.5"/>
      </svg>
    </div>
    <div class="titles">
      <span class="eyebrow">Desired State Configuration</span>
      <h1>TugDSC Pull Server</h1>
    </div>
  </div>

  <div class="status reveal d2">
    <span class="dot" aria-hidden="true"></span>
    Operational
  </div>

  <p class="lede reveal d2">
    This endpoint hosts a <strong>Desired State Configuration</strong> pull server for
    Windows PowerShell nodes &mdash; both modern (WMF&nbsp;5.x, registration-key) and
    legacy (WMF&nbsp;4.0, ConfigurationId). Instead of pushing settings out, each machine
    reaches in on its own schedule to fetch the configuration it should be in.
  </p>
  <p class="lede reveal d2">
    On every check-in a node pulls its assigned configuration, downloads any resource
    modules it still needs, brings itself into that state, and sends back a report — so
    drift is corrected automatically between runs rather than left to accumulate.
  </p>

  <ol class="flow reveal d3">
    <li><b>Register</b><span>Node enrols with a shared key</span></li>
    <li><b>Pull</b><span>Fetches config &amp; modules</span></li>
    <li><b>Apply</b><span>Converges to desired state</span></li>
    <li><b>Report</b><span>Sends status back here</span></li>
  </ol>

  <div class="cards">
    <div class="card reveal d4">
      <div class="ico" aria-hidden="true">
        <svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.6" stroke-linecap="round" stroke-linejoin="round">
          <path d="M5 3h9l5 5v13H5V3Z"/>
          <path d="M14 3v5h5"/>
          <path d="M8.5 13h7M8.5 16.5h7"/>
        </svg>
      </div>
      <h2>Configurations</h2>
      <p>Delivers compiled MOF configurations to registered nodes, matched by name.</p>
      <span class="meta">verified by SHA-256 checksum</span>
    </div>

    <div class="card reveal d5">
      <div class="ico" aria-hidden="true">
        <svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.6" stroke-linecap="round" stroke-linejoin="round">
          <path d="M12 2.5 21 7.5v9L12 21.5 3 16.5v-9L12 2.5Z"/>
          <path d="M3 7.5 12 12.5l9-5M12 12.5v9"/>
        </svg>
      </div>
      <h2>Modules</h2>
      <p>Serves the DSC resource modules a node needs before it can apply a configuration.</p>
      <span class="meta">versioned resource packages</span>
    </div>

    <div class="card reveal d6">
      <div class="ico" aria-hidden="true">
        <svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.6" stroke-linecap="round" stroke-linejoin="round">
          <path d="M7 4h10a2 2 0 0 1 2 2v14l-3-2-2 2-2-2-2 2-2-2-2 2V6a2 2 0 0 1 2-2Z"/>
          <path d="M9 9h6M9 12.5h6"/>
        </svg>
      </div>
      <h2>Reports</h2>
      <p>Collects a status report from each node after every consistency check.</p>
      <span class="meta">kept per node for review</span>
    </div>
  </div>

  <div class="runtime reveal d7">
    <span class="label">Runtime</span>
    <span class="chip"><b>DSC</b> Pull Protocol v1 &amp; v2</span>
    <span class="chip">Registration-key &amp; ConfigurationId</span>
    <span class="chip"><b>.NET</b> 8 · Kestrel</span>
    <span class="chip">MOF over HTTPS</span>
  </div>

  <footer class="reveal d7">TugDSC · running</footer>
</main>
</body>
</html>
"""";

        #endregion -- Constants --

        #region -- Fields --

        protected ILogger<Startup> _logger;

        protected IConfiguration _config;

        #endregion -- Fields --

        #region -- Constructors --

        public Startup(IWebHostEnvironment env, ILoggerFactory loggerFactory)
        {
            // Start with a pre-logger till the final
            // logging config is finalized down below
            _logger = StartupLogger.CreateLogger<Startup>();

            // This is ugly as hell but unfortunately, we could not find another
            // way to pass this along from Program to other parts of the app via DI
            var args = Program.CommandLineArgs?.ToArray();

            _logger.LogInformation("Resolving final runtime configuration");
            _config = ResolveAppConfig(args);

            // TODO: We may need to adjust based on changes in .NET 2.0
            ConfigureLogging(env, loggerFactory);
        }

        #endregion -- Constructors --

        #region -- Methods --

        // This method gets called by the runtime. Use this
        // method to add services to the container.  For more
        // information on how to configure your application,
        // visit https://go.microsoft.com/fwlink/?LinkID=398940
        public void ConfigureServices(IServiceCollection services)
        {
            _logger.LogInformation("Configuring services registry");

            // The DSC protocol's registration/authz filters (DscRegKeyAuthzFilter,
            // VeryStrictInputFilter) read the request body synchronously via
            // Stream.CopyTo, which Kestrel disallows by default since ASP.NET Core 2.1
            // (this code predates that change). Payloads here are small JSON bodies,
            // so allowing sync IO is an acceptable, minimal fix vs. rewriting the filters
            // to be fully async.
            services.Configure<Microsoft.AspNetCore.Server.Kestrel.Core.KestrelServerOptions>(
                options => options.AllowSynchronousIO = true);

            // Enable and bind to strongly-typed configuration
            var appSettings = _config.GetSection(nameof(AppSettings));
            services.AddSingleton<IConfiguration>(appSettings);
            services.AddOptions();
            services.Configure<AppSettings>(appSettings);
            services.Configure<ChecksumSettings>(
                    appSettings.GetSection(nameof(AppSettings.Checksum)));
            services.Configure<AuthzSettings>(
                    appSettings.GetSection(nameof(AppSettings.Authz)));
            services.Configure<HandlerSettings>(
                    appSettings.GetSection(nameof(AppSettings.Handler)));

            // Register a single instance of each filter type we'll use down below
            services.AddSingleton<DscRegKeyAuthzFilter.IAuthzStorageHandler,
                    DscRegKeyAuthzFilter.LocalAuthzStorageHandler>();
            services.AddSingleton<DscRegKeyAuthzFilter>();
            services.AddSingleton<StrictInputFilter>();
            services.AddSingleton<VeryStrictInputFilter>();

            // Add MVC-supporting services
            _logger.LogInformation("Adding MVC services");
            services.AddMvc(options =>
            {
                // Add the filter by service type reference
                options.Filters.AddService(typeof(DscRegKeyAuthzFilter));
                options.Filters.AddService(typeof(VeryStrictInputFilter));
            }).AddNewtonsoftJson(options =>
                {
                    // This enables converting Enums to/from their string names instead
                    // of their numerical value, based on:
                    //    * https://www.exceptionnotfound.net/serializing-enumerations-in-asp-net-web-api/
                    //    * https://siderite.blogspot.com/2016/10/controlling-json-serialization-in-net.html
                    options.SerializerSettings.Converters.Add(new StringEnumConverter());
                });

            
            // Register the Provider Managers 
            services.AddSingleton<ChecksumAlgorithmManager>();
            services.AddSingleton<DscHandlerManager>();

            // Register the Helpers
            services.AddSingleton<ChecksumHelper>();
            services.AddSingleton<DscHandlerHelper>();
        }

        // This method gets called by the runtime. Use this
        // method to configure the HTTP request pipeline.
        public void Configure(IServiceProvider serviceProvider,
                IApplicationBuilder app, IWebHostEnvironment env)
        {
            // set development option
            if (env.IsDevelopment())
            {
                app.UseDeveloperExceptionPage();
            }

            // endpoint routing (replaces obsolete app.UseMvc from ASP.NET Core <=2.x)
            app.UseRouting();
            app.UseEndpoints(endpoints =>
            {

                // Default route: Fluent-styled status page (see WelcomePage below)
                endpoints.MapGet("/", context =>
                {
                    context.Response.ContentType = "text/html; charset=utf-8";
                    return context.Response.WriteAsync(WelcomePage);
                });

                // Server version info
                endpoints.MapGet("/version", context =>
                {
                    var version = GetType().GetTypeInfo().Assembly.GetName().Version;
                    return context.Response.WriteAsync($@"{{""version"":""{version}""}}");
                });

                // Attribute-routed DSC protocol controllers
                endpoints.MapControllers();
            });

            // Resolve some DI classes to make sure they're ready to go when needed and
            // forces any possible resolution errors to invoke earlier rather than later
            serviceProvider.GetRequiredService<ChecksumHelper>();
            serviceProvider.GetRequiredService<DscHandlerHelper>();
        }

        protected IConfiguration ResolveAppConfig(string[] args = null)
        {
            var basePath = Directory.GetCurrentDirectory();
            if (Program.RunAsService)
                basePath = Path.GetDirectoryName(Process.GetCurrentProcess().MainModule.FileName);

            // Resolve the app config filenames
            var jsonFile = args?.FirstOrDefault(x => x.StartsWith(APP_CONFIG_CLI_OVERRIDE))
                    ?.Substring(APP_CONFIG_CLI_OVERRIDE.Length) ?? APP_CONFIG_FILENAME;
             _logger.LogInformation("Resolved app config file as [{0}]", jsonFile);
            var userFile = args?.FirstOrDefault(x => x.StartsWith(APP_USER_CONFIG_CLI_OVERRIDE))
                    ?.Substring(APP_USER_CONFIG_CLI_OVERRIDE.Length) ?? APP_USER_CONFIG_FILENAME;
             _logger.LogInformation("Resolved user-local app config file as [{0}]", userFile);

            if (args?.Length > 0)
            {
                var jsonFileOverride = args.FirstOrDefault(x => x.StartsWith(APP_CONFIG_CLI_OVERRIDE));
                var userFileOverride = args.FirstOrDefault(x => x.StartsWith("--userconfig="));

                jsonFile = args.FirstOrDefault(x => x.StartsWith(APP_CONFIG_CLI_OVERRIDE))?.Substring(APP_CONFIG_CLI_OVERRIDE.Length) ?? jsonFile;
            }

            // Resolve the runtime configuration settings
            var appConfigBuilder = new ConfigurationBuilder();
            // Base path for any file-based config sources
            appConfigBuilder.SetBasePath(basePath);
            // Default location for all configuration settings
            appConfigBuilder.AddJsonFile(jsonFile, optional: false);
            // Optional location for user-specific local overrides
            appConfigBuilder.AddJsonFile(userFile, optional: true);
            // Allows overriding any setting using envVars that being with TUG_CFG_
            appConfigBuilder.AddEnvironmentVariables(prefix: APP_CONFIG_ENV_PREFIX);
            // A good place to store secrets for dev/test
            appConfigBuilder.AddUserSecrets<Startup>();

            if (args != null)
            {
                var configArgs = args.Where(x => x.StartsWith(APP_CONFIG_CLI_PREFIX))
                        .Select(x => x.Substring(APP_CONFIG_CLI_PREFIX.Length)).ToArray();
                // Last but not least, allow overriding with CLI arguments
                appConfigBuilder.AddCommandLine(configArgs);
            }

            return appConfigBuilder.Build();
        }

        protected void ConfigureLogging(IWebHostEnvironment env, ILoggerFactory loggerFactory)
        {
            var logSettings = _config
                ?.GetSection(nameof(LogSettings))
                ?.Get<LogSettings>();

            _logger.LogInformation("Applying logging configuration");

            if (logSettings != null)
            {
                if (logSettings.LogType.HasFlag(LogType.Console)) {
                    // In modern ASP.NET Core, logging providers are configured at host
                    // build time (see Program.BuildWebHost -> ConfigureLogging.AddConsole);
                    // providers cannot be added to an already-built ILoggerFactory.
                    _logger.LogInformation("  * Console Logging enabled");
                }

                // TODO: Resolve which logger to use
                // if (logSettings.LogType.HasFlag(LogType.NLog)) {
                //     var configPath = Path.Combine(Directory.GetCurrentDirectory(), "nlog.config");
                //     _logger.LogInformation($"  * enabling NLog with config=[{configPath}]");
                //     loggerFactory.AddNLog();
                //     env.ConfigureNLog(configPath);
                // }
            }

            // TODO: We may need to adjust based on changes in .NET 2.0            

            // Initiate and switch to runtime logging
            _logger.LogInformation("Instantiating runtime logging");
            _logger.LogInformation("********** Ceasing STARTUP LOGGING **********");
            _logger.LogInformation("");


            _logger = loggerFactory.CreateLogger<Startup>();
            _logger.LogInformation("********** Commencing RUNTIME LOGGING **********");
        }

        #endregion -- Methods --
    }
}