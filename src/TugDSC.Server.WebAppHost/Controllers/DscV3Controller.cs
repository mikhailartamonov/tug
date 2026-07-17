// PowerShell.org Tug DSC Pull Server
// Copyright (c) The DevOps Collective, Inc.  All rights reserved.
// Licensed under the MIT license.  See the LICENSE file in the project root for more information.

using System;
using System.IO;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json.Linq;
using TugDSC.Server.Mvc;
using TugDSC.Server.Util;

namespace TugDSC.Server.WebAppHost.Controllers
{
    /// <summary>
    /// Collection endpoint for the self-hosted DSC v3 pull agent
    /// (<c>contrib/dsc-v3/pull-agent.ps1</c>). DSC v3 itself has no reporting
    /// protocol; the agent just POSTs its run result as JSON and this stores it
    /// under <c>Reports/v3/&lt;node&gt;/</c> — the same tree the v1/v2 reports
    /// live in, so existing backups pick it up.
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
    }
}
