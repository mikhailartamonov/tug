// PowerShell.org Tug DSC Pull Server
// Copyright (c) The DevOps Collective, Inc.  All rights reserved.
// Licensed under the MIT license.  See the LICENSE file in the project root for more information.

using System;
using System.IO;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using TugDSC.Server.Mvc;
using TugDSC.Server.Util;

namespace TugDSC.Server.WebAppHost.Controllers
{
    /// <summary>
    /// Implements the legacy WMF 4.0 ("v1.0/1.1") DSC pull protocol used by
    /// PowerShell 4.0 nodes, per [MS-DSCPM] section 3.1/3.3: configurations are
    /// addressed by a <c>ConfigurationId</c> GUID, there is no agent
    /// registration and no HMAC authorization (unlike the v2
    /// <see cref="DscController"/>).
    /// </summary>
    /// <remarks>
    /// A v4 LCM is pointed here by a meta-config whose
    /// <c>DownloadManagerCustomData.ServerUrl</c> ends in a path (classically
    /// <c>PSDSCPullServer.svc</c>); the LCM then appends the OData-style
    /// operations:
    /// <list type="bullet">
    /// <item><c>POST .../Action(ConfigurationId='&lt;guid&gt;')/GetAction</c> — status check
    ///   (request body <c>{Checksum, ChecksumAlgorithm, NodeCompliant, ...}</c>,
    ///   response body <c>{"value":"GetConfiguration"|"OK"}</c>).</item>
    /// <item><c>GET .../Action(ConfigurationId='&lt;guid&gt;')/ConfigurationContent</c> —
    ///   download the MOF with <c>Checksum</c>/<c>ChecksumAlgorithm</c> headers.</item>
    /// </list>
    /// Configurations are served from the same store as v2, keyed by the GUID:
    /// deploy the MOF as <c>&lt;ConfigurationId&gt;.mof</c>.
    ///
    /// The v2 global filters (registration/HMAC, very-strict input) no-op here
    /// because these actions don't bind a <c>DscRequest</c> model.
    /// </remarks>
    public class DscV1Controller : Controller
    {
        // The path clients put in DownloadManagerCustomData.ServerUrl.
        private const string SVC = "PSDSCPullServer.svc";

        private readonly ILogger<DscV1Controller> _logger;
        private readonly IDscHandler _dscHandler;

        public DscV1Controller(ILogger<DscV1Controller> logger, DscHandlerHelper dscHelper)
        {
            _logger = logger;
            _dscHandler = dscHelper.DefaultHandler;
        }

        /// <summary>
        /// The v1 GetAction request body ([MS-DSCPM] 3.3.5.1.1.1) — a flat
        /// object, not the v2 <c>ClientStatus</c> array.
        /// </summary>
        public class GetActionBody
        {
            public string Checksum { get; set; }
            public string ChecksumAlgorithm { get; set; }
            public bool? NodeCompliant { get; set; }
            public int? StatusCode { get; set; }
            public string ConfigurationName { get; set; }
        }

        /// <summary>
        /// v1 status check. Tells the node whether it needs to (re)download the
        /// configuration bound to <paramref name="configurationId"/>.
        /// </summary>
        [HttpPost(SVC + "/Action(ConfigurationId='{configurationId}')/GetAction")]
        public IActionResult GetActionV1(string configurationId, [FromBody] GetActionBody body)
        {
            _logger.LogInformation("v1 GetAction ConfigurationId=[{cid}]", configurationId);

            var content = _dscHandler.GetConfiguration(Guid.Empty, configurationId);
            if (content == null)
                return NotFound();

            try
            {
                // Empty/blank checksum on the first pull -> always fetch.
                var status = !string.IsNullOrEmpty(body?.Checksum)
                        && string.Equals(body.Checksum, content.Checksum, StringComparison.OrdinalIgnoreCase)
                    ? "OK"
                    : "GetConfiguration";

                _logger.LogInformation("v1 GetAction -> {status}", status);

                // [MS-DSCPM] 3.3.5.1.1.2 / section 6: the response body is
                // { "value": "<status>" } (lowercase "value"). Serialize
                // explicitly so ASP.NET Core's camelCase default doesn't matter
                // and the v2 responses stay untouched.
                var json = JsonConvert.SerializeObject(new { value = status });
                return Content(json, "application/json");
            }
            finally
            {
                content.Content?.Dispose();
            }
        }

        /// <summary>
        /// v1 configuration download. Serves <c>&lt;ConfigurationId&gt;.mof</c>
        /// with the DSC <c>Checksum</c>/<c>ChecksumAlgorithm</c> response headers.
        /// </summary>
        [HttpGet(SVC + "/Action(ConfigurationId='{configurationId}')/ConfigurationContent")]
        public IActionResult GetConfigurationContentV1(string configurationId)
        {
            _logger.LogInformation("v1 ConfigurationContent ConfigurationId=[{cid}]", configurationId);

            var content = _dscHandler.GetConfiguration(Guid.Empty, configurationId);
            if (content == null)
                return NotFound();

            Response.Headers["Checksum"] = content.Checksum;
            Response.Headers["ChecksumAlgorithm"] = content.ChecksumAlgorithm;
            return File(content.Content, "application/octet-stream");
        }

        /// <summary>
        /// v1 status report ([MS-DSCPM] 3.4). The node POSTs a JSON report after
        /// a consistency run. Stored under <c>Reports/&lt;ConfigurationId&gt;/</c>
        /// (same layout as v2, so the same backups pick it up). The spec is
        /// inconsistent about the segment name (<c>Node</c> vs <c>Nodes</c>), so
        /// both are accepted.
        /// </summary>
        [HttpPost(SVC + "/Nodes(ConfigurationId='{configurationId}')/SendStatusReport")]
        [HttpPost(SVC + "/Node(ConfigurationId='{configurationId}')/SendStatusReport")]
        public IActionResult SendStatusReportV1(string configurationId, [FromBody] JObject body)
        {
            _logger.LogInformation("v1 SendStatusReport ConfigurationId=[{cid}]", configurationId);
            try
            {
                // The reports directory is a property of the file-based handler;
                // read it reflectively to avoid coupling to the concrete type.
                var reportsPath = _dscHandler.GetType().GetProperty("ReportsPath")?
                        .GetValue(_dscHandler) as string;
                if (!string.IsNullOrEmpty(reportsPath) && body != null)
                {
                    var jobId = (string)body["JobId"];
                    if (string.IsNullOrEmpty(jobId))
                        jobId = Guid.NewGuid().ToString();
                    var dir = Path.Combine(reportsPath, configurationId);
                    Directory.CreateDirectory(dir);
                    System.IO.File.WriteAllText(Path.Combine(dir, jobId + ".json"), body.ToString());
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "v1 SendStatusReport store failed (non-fatal)");
            }

            return Ok();
        }
    }
}
