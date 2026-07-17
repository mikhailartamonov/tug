// PowerShell.org Tug DSC Pull Server
// Copyright (c) The DevOps Collective, Inc.  All rights reserved.
// Licensed under the MIT license.  See the LICENSE file in the project root for more information.

using System;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json.Linq;
using TugDSC.Server.Mvc;
using TugDSC.Server.Util;

namespace TugDSC.Server.WebAppHost.Controllers
{
    /// <summary>
    /// Collection endpoints for the self-hosted DSC v3 pull agent
    /// (<c>contrib/dsc-v3/pull-agent.ps1</c>). DSC v3 has no reporting protocol,
    /// so the agent POSTs:
    /// <list type="bullet">
    /// <item><c>v3/report</c> — a JSON run result (stored under <c>Reports/v3/&lt;node&gt;/</c>).</item>
    /// <item><c>v3/enroll</c> — on first contact, the node's exported current-state
    ///   document (a YAML/JSON snapshot, stored under <c>Reports/v3-enroll/&lt;node&gt;/</c>),
    ///   used as a baseline seed.</item>
    /// </list>
    /// Both land in the reports tree, so the existing backup picks them up.
    /// </summary>
    public class DscV3Controller : Controller
    {
        private static readonly Regex SafeName = new Regex(@"[^A-Za-z0-9._-]", RegexOptions.Compiled);

        private readonly ILogger<DscV3Controller> _logger;
        private readonly IDscHandler _dscHandler;

        public DscV3Controller(ILogger<DscV3Controller> logger, DscHandlerHelper dscHelper)
        {
            _logger = logger;
            _dscHandler = dscHelper.DefaultHandler;
        }

        [HttpPost("v3/report")]
        public IActionResult V3Report([FromBody] JObject body)
        {
            var node = SafeName.Replace((string)body?["node"] ?? "unknown", "_");
            if (string.IsNullOrEmpty(node)) node = "unknown";
            _logger.LogInformation("v3 report from node=[{node}]", node);

            try
            {
                var reportsPath = _dscHandler.GetType().GetProperty("ReportsPath")?
                        .GetValue(_dscHandler) as string;
                if (string.IsNullOrEmpty(reportsPath) || body == null)
                    return Ok();   // accept but nothing to persist

                var dir = Path.Combine(reportsPath, "v3", node);
                Directory.CreateDirectory(dir);
                // server-stamped filename keeps reports ordered and unique
                var file = Path.Combine(dir, $"{DateTime.UtcNow:yyyyMMdd_HHmmssfff}.json");
                System.IO.File.WriteAllText(file, body.ToString());
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "v3 report store failed (non-fatal)");
            }

            return Ok();
        }

        /// <summary>
        /// First-contact enrollment: the node uploads the document it exported of
        /// its own current state (`dsc config export` / `winget configure export`).
        /// The body is opaque YAML/JSON, so it's read raw and stored verbatim under
        /// <c>Reports/v3-enroll/&lt;node&gt;/</c> as a baseline seed.
        /// </summary>
        [HttpPost("v3/enroll")]
        public async Task<IActionResult> V3Enroll()
        {
            var node = SafeName.Replace(Request.Headers["X-Node"].ToString(), "_");
            if (string.IsNullOrEmpty(node)) node = "unknown";
            var engine = SafeName.Replace(Request.Headers["X-Engine"].ToString(), "_");
            var ext = string.Equals(Request.Headers["X-Format"].ToString().Trim(),
                    "json", StringComparison.OrdinalIgnoreCase) ? "json" : "yaml";
            _logger.LogInformation("v3 enroll snapshot from node=[{node}] engine=[{engine}]", node, engine);

            string body;
            using (var reader = new StreamReader(Request.Body))
                body = await reader.ReadToEndAsync();

            try
            {
                var reportsPath = _dscHandler.GetType().GetProperty("ReportsPath")?
                        .GetValue(_dscHandler) as string;
                if (!string.IsNullOrEmpty(reportsPath) && !string.IsNullOrEmpty(body))
                {
                    var dir = Path.Combine(reportsPath, "v3-enroll", node);
                    Directory.CreateDirectory(dir);
                    var file = Path.Combine(dir, $"{DateTime.UtcNow:yyyyMMdd_HHmmssfff}.{ext}");
                    await System.IO.File.WriteAllTextAsync(file, body);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "v3 enroll store failed (non-fatal)");
            }

            return Ok();
        }
    }
}
