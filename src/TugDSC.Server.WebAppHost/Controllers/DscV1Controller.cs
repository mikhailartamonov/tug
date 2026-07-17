// PowerShell.org Tug DSC Pull Server
// Copyright (c) The DevOps Collective, Inc.  All rights reserved.
// Licensed under the MIT license.  See the LICENSE file in the project root for more information.

using System;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using TugDSC.Server.Mvc;
using TugDSC.Server.Util;

namespace TugDSC.Server.WebAppHost.Controllers
{
    /// <summary>
    /// Implements the legacy WMF 4.0 ("v1") DSC pull protocol used by
    /// PowerShell 4.0 nodes: configurations are addressed by a
    /// <c>ConfigurationId</c> GUID, there is no agent registration and no HMAC
    /// authorization (unlike the v2 <see cref="DscController"/>).
    /// </summary>
    /// <remarks>
    /// A v4 LCM is pointed here by a meta-config whose
    /// <c>DownloadManagerCustomData.ServerUrl</c> ends in a path (the classic
    /// <c>PSDSCPullServer.svc</c>); the LCM then appends OData-style operations,
    /// e.g. <c>POST .../Action(ConfigurationId='&lt;guid&gt;')/GetAction</c>.
    /// Configurations are served from the same store as v2, keyed by the GUID:
    /// deploy the MOF as <c>&lt;ConfigurationId&gt;.mof</c> (both the flat and
    /// SHARED paths, like any other config).
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

        public class ClientStatusItem
        {
            public string Checksum { get; set; }
            public string ChecksumAlgorithm { get; set; }
        }

        public class GetActionBody
        {
            public ClientStatusItem[] ClientStatus { get; set; }
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
                var clientChecksum = body?.ClientStatus != null && body.ClientStatus.Length > 0
                        ? body.ClientStatus[0].Checksum
                        : null;

                // Empty/blank on the first pull -> always fetch.
                var status = !string.IsNullOrEmpty(clientChecksum)
                        && string.Equals(clientChecksum, content.Checksum, StringComparison.OrdinalIgnoreCase)
                    ? "OK"
                    : "GetConfiguration";

                _logger.LogInformation("v1 GetAction -> {status}", status);
                return Json(new { NodeStatus = status });
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
        [HttpGet(SVC + "/Configurations(ConfigurationId='{configurationId}')/ConfigurationContent")]
        public IActionResult GetConfigurationV1(string configurationId)
        {
            _logger.LogInformation("v1 GetConfiguration ConfigurationId=[{cid}]", configurationId);

            var content = _dscHandler.GetConfiguration(Guid.Empty, configurationId);
            if (content == null)
                return NotFound();

            Response.Headers["Checksum"] = content.Checksum;
            Response.Headers["ChecksumAlgorithm"] = content.ChecksumAlgorithm;
            return File(content.Content, "application/octet-stream");
        }
    }
}
