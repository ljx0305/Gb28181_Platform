﻿// ============================================================================
// FileName: SIPRegistrarDaemon.cs
//
// Description:
// A daemon to configure and start a SIP Registration Agent.
//
// Author(s):
// Aaron Clauson
//
// History:
// 29 Mar 2009	Aaron Clauson	Created.
//
// License: 
// This software is licensed under the BSD License http://www.opensource.org/licenses/bsd-license.php
//
// Copyright (c) 2009 Aaron Clauson (aaronc@blueface.ie), Blue Face Ltd, Dublin, Ireland (www.blueface.ie)
// All rights reserved.
//
// Redistribution and use in source and binary forms, with or without modification, are permitted provided that 
// the following conditions are met:
//
// Redistributions of source code must retain the above copyright notice, this list of conditions and the following disclaimer. 
// Redistributions in binary form must reproduce the above copyright notice, this list of conditions and the following 
// disclaimer in the documentation and/or other materials provided with the distribution. Neither the name of Blue Face Ltd. 
// nor the names of its contributors may be used to endorse or promote products derived from this software without specific 
// prior written permission. 
//
// THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS "AS IS" AND ANY EXPRESS OR IMPLIED WARRANTIES, INCLUDING, 
// BUT NOT LIMITED TO, THE IMPLIED WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE ARE DISCLAIMED. 
// IN NO EVENT SHALL THE COPYRIGHT OWNER OR CONTRIBUTORS BE LIABLE FOR ANY DIRECT, INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, 
// OR CONSEQUENTIAL DAMAGES (INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR SERVICES; LOSS OF USE, DATA, 
// OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER CAUSED AND ON ANY THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY, 
// OR TORT (INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE OF THIS SOFTWARE, EVEN IF ADVISED OF THE 
// POSSIBILITY OF SUCH DAMAGE.
// ============================================================================

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Xml;
using SIPSorcery.Persistence;
using SIPSorcery.SIP;
using SIPSorcery.SIP.App;
using SIPSorcery.Servers;
using SIPSorcery.Sys;
using log4net;
using SIPSorcery.Servers.SIPMessage;

namespace Gb28181_Client.Register
{
    public class SIPRegistrarDaemon
    {
        private ILog logger = AppState.logger;

        private XmlNode m_sipRegistrarSocketsNode = SIPRegistrarState.SIPRegistrarSocketsNode;
        private XmlNode m_userAgentsConfigNode = SIPRegistrarState.UserAgentsConfigNode;
        private int m_monitorLoopbackPort = SIPRegistrarState.MonitorLoopbackPort;
        private int m_maximumAccountBindings = SIPRegistrarState.MaximumAccountBindings;
        private IPEndPoint m_natKeepAliveRelaySocket = SIPRegistrarState.NATKeepAliveRelaySocket;
        //private string m_switchboardCertificateName = SIPRegistrarState.SwitchboardCertificateName;
        private string m_switchboardUserAgentPrefix = SIPRegistrarState.SwitchboardUserAgentPrefix;
        private int m_threadCount = SIPRegistrarState.ThreadCount;

        private SIPTransport m_sipTransport;
        private RegistrarCore m_registrarCore;
        internal SIPMessageCore m_msgCore;
        private SIPRegistrarBindingsManager m_registrarBindingsManager;
        private UdpClient m_natKeepAliveSender;

        private GetCanonicalDomainDelegate GetCanonicalDomain_External;
        private SIPAssetGetDelegate<SIPAccount> GetSIPAccount_External;
        //private SIPAssetGetFromDirectQueryDelegate<SIPAccount> GetSIPAccountFromQuery_External;
        private SIPAssetPersistor<SIPRegistrarBinding> m_registrarBindingsPersistor;
        private SIPAuthenticateRequestDelegate SIPAuthenticateRequest_External;

        public SIPRegistrarDaemon(
            GetCanonicalDomainDelegate getDomain,
            SIPAssetGetDelegate<SIPAccount> getSIPAccount,
            //SIPAssetGetFromDirectQueryDelegate<SIPAccount> getSIPAccountFromQuery,
            SIPAssetPersistor<SIPRegistrarBinding> registrarBindingsPersistor,
            SIPAuthenticateRequestDelegate sipRequestAuthenticator)
        {
            GetCanonicalDomain_External = getDomain;
            GetSIPAccount_External = getSIPAccount;
            //GetSIPAccountFromQuery_External = getSIPAccountFromQuery;
            m_registrarBindingsPersistor = registrarBindingsPersistor;
            SIPAuthenticateRequest_External = sipRequestAuthenticator;
        }

        public void Start()
        {
            try
            {
                logger.Debug("SIP Registrar daemon starting...");

                // Pre-flight checks.
                if (m_sipRegistrarSocketsNode == null || m_sipRegistrarSocketsNode.ChildNodes.Count == 0)
                {
                    throw new ApplicationException("The SIP Registrar cannot start without at least one socket specified to listen on, please check config file.");
                }


                // Configure the SIP transport layer.
                m_sipTransport = new SIPTransport(SIPDNSManager.ResolveSIPService, new SIPTransactionEngine(), false);
                m_sipTransport.PerformanceMonitorPrefix = SIPSorceryPerformanceMonitor.REGISTRAR_PREFIX;
                List<SIPChannel> sipChannels = SIPTransportConfig.ParseSIPChannelsNode(m_sipRegistrarSocketsNode);
                m_sipTransport.AddSIPChannel(sipChannels);

                // Create and configure the SIP Registrar core.
                if (m_natKeepAliveRelaySocket != null)
                {
                    m_natKeepAliveSender = new UdpClient();
                }

                SIPUserAgentConfigurationManager userAgentConfigManager = new SIPUserAgentConfigurationManager(m_userAgentsConfigNode);
                if (m_userAgentsConfigNode == null)
                {
                    logger.Warn("The UserAgent config's node was missing.");
                }
                m_registrarBindingsManager = new SIPRegistrarBindingsManager( m_registrarBindingsPersistor,  m_maximumAccountBindings, userAgentConfigManager);
                m_registrarBindingsManager.Start();

                m_registrarCore = new RegistrarCore(m_sipTransport, m_registrarBindingsManager, GetSIPAccount_External, GetCanonicalDomain_External, true, true, userAgentConfigManager, SIPAuthenticateRequest_External, m_switchboardUserAgentPrefix);
                m_registrarCore.Start(m_threadCount);
                m_sipTransport.SIPTransportRequestReceived += m_registrarCore.AddRegisterRequest;

                m_msgCore = new SIPMessageCore(m_sipTransport, userAgentConfigManager.DefaultUserAgent);
                m_sipTransport.SIPTransportRequestReceived += m_msgCore.AddMessageRequest;
                m_sipTransport.SIPTransportResponseReceived += m_msgCore.AddMessageResponse;

                Console.ForegroundColor = ConsoleColor.Green;
                logger.Debug("SIP Registrar successfully started.");
            }
            catch (Exception excp)
            {
                logger.Error("Exception SIPRegistrarDaemon Start. " + excp.Message);
            }
        }
        public void Stop()
        {
            try
            {
                logger.Debug("SIP Registrar daemon stopping...");

                logger.Debug("Shutting down Registrar Bindings Manager.");
                m_registrarBindingsManager.Stop();

                logger.Debug("Shutting down SIP Transport.");
                m_sipTransport.Shutdown();

                logger.Debug("SIP Registrar daemon stopped.");
            }
            catch (Exception excp)
            {
                logger.Error("Exception SIPRegistrarDaemon Stop. " + excp.Message);
            }
        }
    }
}