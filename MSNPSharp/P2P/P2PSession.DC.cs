#region Copyright (c) 2002-2011, Bas Geertsema, Xih Solutions (http://www.xihsolutions.net), Thiago.Sayao, Pang Wu, Ethem Evlice
/*
Copyright (c) 2002-2011, Bas Geertsema, Xih Solutions
(http://www.xihsolutions.net), Thiago.Sayao, Pang Wu, Ethem Evlice.
All rights reserved. http://code.google.com/p/msnp-sharp/

Redistribution and use in source and binary forms, with or without
modification, are permitted provided that the following conditions are met:

* Redistributions of source code must retain the above copyright notice,
  this list of conditions and the following disclaimer.
* Redistributions in binary form must reproduce the above copyright notice,
  this list of conditions and the following disclaimer in the documentation
  and/or other materials provided with the distribution.
* Neither the names of Bas Geertsema or Xih Solutions nor the names of its
  contributors may be used to endorse or promote products derived from this
  software without specific prior written permission.

THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS 'AS IS'
AND ANY EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE
IMPLIED WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE
ARE DISCLAIMED. IN NO EVENT SHALL THE COPYRIGHT OWNER OR CONTRIBUTORS BE
LIABLE FOR ANY DIRECT, INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR
CONSEQUENTIAL DAMAGES (INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF
SUBSTITUTE GOODS OR SERVICES; LOSS OF USE, DATA, OR PROFITS; OR BUSINESS
INTERRUPTION) HOWEVER CAUSED AND ON ANY THEORY OF LIABILITY, WHETHER IN
CONTRACT, STRICT LIABILITY, OR TORT (INCLUDING NEGLIGENCE OR OTHERWISE)
ARISING IN ANY WAY OUT OF THE USE OF THIS SOFTWARE, EVEN IF ADVISED OF
THE POSSIBILITY OF SUCH DAMAGE. 
*/
#endregion

using System;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Diagnostics;
using System.Collections.Generic;

namespace MSNPSharp.P2P
{
    using MSNPSharp;
    using MSNPSharp.Core;

    public enum DCNonceType
    {
        None = 0,
        Plain = 1,
        Sha1 = 2
    }

    partial class P2PSession
    {
        public static Guid ParseDCNonce(MimeDictionary bodyValues, out DCNonceType dcNonceType)
        {
            dcNonceType = DCNonceType.None;
            Guid nonce = Guid.Empty;

            if (bodyValues.ContainsKey("Hashed-Nonce"))
            {
                nonce = new Guid(bodyValues["Hashed-Nonce"].Value);
                dcNonceType = DCNonceType.Sha1;
            }
            else if (bodyValues.ContainsKey("Nonce"))
            {
                nonce = new Guid(bodyValues["Nonce"].Value);
                dcNonceType = DCNonceType.Plain;
            }

            return nonce;
        }

        internal static void ProcessDirectInvite(
            SLPMessage slp,
            NSMessageHandler nsMessageHandler,
            P2PSession startupSession)
        {
            if (slp.ContentType == "application/x-msnmsgr-transreqbody")
                ProcessDirectReqInvite(slp, nsMessageHandler, startupSession);
            else if (slp.ContentType == "application/x-msnmsgr-transrespbody")
                ProcessDirectRespInvite(slp, nsMessageHandler, startupSession);
            else if (slp.ContentType == "application/x-msnmsgr-transdestaddrupdate")
                ProcessDirectAddrUpdate(slp, nsMessageHandler, startupSession);
        }

        private void DirectNegotiationSuccessful()
        {
            Trace.WriteLineIf(Settings.TraceSwitch.TraceVerbose, "Direct connection negotiation was successful", GetType().Name);
        }

        internal void DirectNegotiationFailed()
        {
            Trace.WriteLineIf(Settings.TraceSwitch.TraceVerbose, "Direct connection negotiation was unsuccessful", GetType().Name);

            if (p2pBridge != null)
                p2pBridge.ResumeSending(this);
        }

        private static string ConnectionType(NSMessageHandler nsMessageHandler, out int netId)
        {
            string connectionType = "Unknown-Connect";
            netId = 0;
            IPEndPoint localEndPoint = nsMessageHandler.LocalEndPoint;
            IPEndPoint externalEndPoint = nsMessageHandler.ExternalEndPoint;

            if (localEndPoint == null || externalEndPoint == null)
            {
            }
            else
            {
                netId = BitConverter.ToInt32(localEndPoint.Address.GetAddressBytes(), 0);

                if (localEndPoint.Address.Equals(externalEndPoint.Address))
                {
                    if (localEndPoint.Port == externalEndPoint.Port)
                        connectionType = "Direct-Connect";
                    else
                        connectionType = "Port-Restrict-NAT";
                }
                else
                {
                    if (localEndPoint.Port == externalEndPoint.Port)
                    {
                        netId = 0;
                        connectionType = "IP-Restrict-NAT";
                    }
                    else
                        connectionType = "Symmetric-NAT";
                }
            }

            return connectionType;
        }

        internal static void SendDirectInvite(
            NSMessageHandler nsMessageHandler,
            P2PBridge p2pBridge,
            P2PSession p2pSession)
        {
            // Only send the direct invite if we're currently using an SBBridge or UUNBridge
            if (!(p2pBridge is SBBridge) && !(p2pBridge is UUNBridge))
                return;

            int netId;
            string connectionType = ConnectionType(nsMessageHandler, out netId);
            Contact remote = p2pSession.Remote;
            P2PVersion ver = p2pSession.Version;
            P2PMessage p2pMessage = new P2PMessage(ver);

            Trace.WriteLineIf(Settings.TraceSwitch.TraceInfo,
                String.Format("Connection type set to {0} for session {1}", connectionType, p2pSession.SessionId.ToString()));

            // Create the message
            SLPRequestMessage slpMessage = new SLPRequestMessage(p2pSession.RemoteContactEPIDString, MSNSLPRequestMethod.INVITE);
            slpMessage.Source = p2pSession.LocalContactEPIDString;
            slpMessage.CSeq = 0;
            slpMessage.CallId = p2pSession.Invitation.CallId;
            slpMessage.MaxForwards = 0;
            slpMessage.ContentType = "application/x-msnmsgr-transreqbody";

            slpMessage.BodyValues["Bridges"] = "TCPv1 SBBridge";
            slpMessage.BodyValues["Capabilities-Flags"] = "1";
            slpMessage.BodyValues["NetID"] = netId.ToString(System.Globalization.CultureInfo.InvariantCulture);
            slpMessage.BodyValues["Conn-Type"] = connectionType;
            slpMessage.BodyValues["TCP-Conn-Type"] = connectionType;
            slpMessage.BodyValues["UPnPNat"] = "false"; // UPNP Enabled
            slpMessage.BodyValues["ICF"] = "false"; // Firewall enabled
            slpMessage.BodyValues["Nat-Trav-Msg-Type"] = "WLX-Nat-Trav-Msg-Direct-Connect-Req";

            // We support Hashed-Nonce ( 2 way handshake )
            remote.GenerateNewDCKeys();
            slpMessage.BodyValues["Hashed-Nonce"] = remote.dcLocalHashedNonce.ToString("B").ToUpper(System.Globalization.CultureInfo.InvariantCulture);

            p2pMessage.InnerMessage = slpMessage;

            if (ver == P2PVersion.P2PV2)
            {
                p2pMessage.V2Header.TFCombination = TFCombination.First;
            }
            else if (ver == P2PVersion.P2PV1)
            {
                p2pMessage.V1Header.Flags = P2PFlag.MSNSLPInfo;
            }

            p2pBridge.Send(p2pSession, remote, p2pSession.RemoteContactEndPointID, p2pMessage, null);

            // Wait a bit, otherwise SLP message queued when called p2pBridge.StopSending(this);
            Thread.CurrentThread.Join(900);

            // Stop sending until we receive a response to the direct invite or the timeout expires
            p2pBridge.StopSending(p2pSession);
        }

        private static void ProcessDirectReqInvite(
            SLPMessage message,
            NSMessageHandler nsMessageHandler,
            P2PSession startupSession)
        {
            if (message.BodyValues.ContainsKey("Bridges") &&
                message.BodyValues["Bridges"].ToString().Contains("TCPv1"))
            {
                SLPStatusMessage slpMessage = new SLPStatusMessage(message.Source, 200, "OK");
                slpMessage.Target = message.Source;
                slpMessage.Source = message.Target;
                slpMessage.Branch = message.Branch;
                slpMessage.CSeq = 1;
                slpMessage.CallId = message.CallId;
                slpMessage.MaxForwards = 0;
                slpMessage.ContentType = "application/x-msnmsgr-transrespbody";
                slpMessage.BodyValues["Bridge"] = "TCPv1";

                Guid remoteGuid = message.FromEndPoint;
                Contact remote = nsMessageHandler.ContactList.GetContact(message.FromEmailAccount, ClientType.PassportMember);
                
                DCNonceType dcNonceType;
                Guid remoteNonce = ParseDCNonce(message.BodyValues, out dcNonceType);
                if (remoteNonce == Guid.Empty) // Plain
                    remoteNonce = remote.dcPlainKey;

                bool hashed = (dcNonceType == DCNonceType.Sha1);
                string nonceFieldName = hashed ? "Hashed-Nonce" : "Nonce";
                Guid myHashedNonce = hashed ? remote.dcLocalHashedNonce : remoteNonce;
                Guid myPlainNonce = remote.dcPlainKey;
                if (dcNonceType == DCNonceType.Sha1)
                {
                    // Remote contact supports Hashed-Nonce
                    remote.dcType = dcNonceType;
                    remote.dcRemoteHashedNonce = remoteNonce;
                }
                else
                {
                    remote.dcType = DCNonceType.Plain;
                    myPlainNonce = remote.dcPlainKey = remote.dcLocalHashedNonce = remote.dcRemoteHashedNonce = remoteNonce;
                }

                // Find host by name
                IPAddress ipAddress = nsMessageHandler.LocalEndPoint.Address;
                int port;

                P2PVersion ver = (message.FromEndPoint != Guid.Empty && message.ToEndPoint != Guid.Empty)
                    ? P2PVersion.P2PV2 : P2PVersion.P2PV1;


                if (false == ipAddress.Equals(nsMessageHandler.ExternalEndPoint.Address) ||
                    (0 == (port = GetNextDirectConnectionPort(ipAddress))))
                {
                    slpMessage.BodyValues["Listening"] = "false";
                    slpMessage.BodyValues[nonceFieldName] = Guid.Empty.ToString("B").ToUpper(System.Globalization.CultureInfo.InvariantCulture);
                }
                else
                {
                    // Let's listen
                    remote.DirectBridge = ListenForDirectConnection(remote, nsMessageHandler, ver, startupSession, ipAddress, port, myPlainNonce, remoteNonce, hashed);

                    slpMessage.BodyValues["Listening"] = "true";
                    slpMessage.BodyValues["Capabilities-Flags"] = "1";
                    slpMessage.BodyValues["IPv6-global"] = string.Empty;
                    slpMessage.BodyValues["Nat-Trav-Msg-Type"] = "WLX-Nat-Trav-Msg-Direct-Connect-Resp";
                    slpMessage.BodyValues["UPnPNat"] = "false";

                    slpMessage.BodyValues["NeedConnectingEndpointInfo"] = "true";

                    slpMessage.BodyValues["Conn-Type"] = "Direct-Connect";
                    slpMessage.BodyValues["TCP-Conn-Type"] = "Direct-Connect";
                    slpMessage.BodyValues[nonceFieldName] = myHashedNonce.ToString("B").ToUpper(System.Globalization.CultureInfo.InvariantCulture);
                    slpMessage.BodyValues["IPv4Internal-Addrs"] = ipAddress.ToString();
                    slpMessage.BodyValues["IPv4Internal-Port"] = port.ToString(System.Globalization.CultureInfo.InvariantCulture);

                    // check if client is behind firewall (NAT-ted)
                    // if so, send the public ip also the client, so it can try to connect to that ip
                    if (!nsMessageHandler.ExternalEndPoint.Address.Equals(nsMessageHandler.LocalEndPoint.Address))
                    {
                        slpMessage.BodyValues["IPv4External-Addrs"] = nsMessageHandler.ExternalEndPoint.Address.ToString();
                        slpMessage.BodyValues["IPv4External-Port"] = port.ToString(System.Globalization.CultureInfo.InvariantCulture);
                    }
                }

                P2PMessage p2pMessage = new P2PMessage(ver);
                p2pMessage.InnerMessage = slpMessage;

                if (ver == P2PVersion.P2PV2)
                {
                    p2pMessage.V2Header.TFCombination = TFCombination.First;
                }
                else if (ver == P2PVersion.P2PV1)
                {
                    p2pMessage.V1Header.Flags = P2PFlag.MSNSLPInfo;
                }

                if (startupSession != null)
                    startupSession.Send(p2pMessage);
                else
                    nsMessageHandler.UUNBridge.Send(null, remote, remoteGuid, p2pMessage, null);
            }
            else
            {
                if (startupSession != null)
                    startupSession.DirectNegotiationFailed();
            }
        }

        private static void ProcessDirectRespInvite(
            SLPMessage message,
            NSMessageHandler nsMessageHandler,
            P2PSession startupSession)
        {
            MimeDictionary bodyValues = message.BodyValues;

            // Check the protocol
            if (bodyValues.ContainsKey("Bridge") &&
                bodyValues["Bridge"].ToString() == "TCPv1" &&
                bodyValues.ContainsKey("Listening") &&
                bodyValues["Listening"].ToString().ToLowerInvariant().IndexOf("true") >= 0)
            {
                Contact remote = nsMessageHandler.ContactList.GetContact(message.FromEmailAccount, ClientType.PassportMember);
                Guid remoteGuid = message.FromEndPoint;

                DCNonceType dcNonceType;
                Guid remoteNonce = ParseDCNonce(message.BodyValues, out dcNonceType);
                bool hashed = (dcNonceType == DCNonceType.Sha1);
                Guid replyGuid = hashed ? remote.dcPlainKey : remoteNonce;

                IPEndPoint[] selectedPoint = SelectIPEndPoint(bodyValues, nsMessageHandler);

                if (selectedPoint != null && selectedPoint.Length > 0)
                {
                    P2PVersion ver = (message.FromEndPoint != Guid.Empty && message.ToEndPoint != Guid.Empty)
                        ? P2PVersion.P2PV2 : P2PVersion.P2PV1;

                    // We must connect to the remote client
                    ConnectivitySettings settings = new ConnectivitySettings();
                    settings.EndPoints = selectedPoint;
                    remote.DirectBridge = CreateDirectConnection(remote, ver, settings, replyGuid, remoteNonce, hashed, nsMessageHandler, startupSession);

                    bool needConnectingEndpointInfo;
                    if (bodyValues.ContainsKey("NeedConnectingEndpointInfo") &&
                        bool.TryParse(bodyValues["NeedConnectingEndpointInfo"], out needConnectingEndpointInfo) &&
                        needConnectingEndpointInfo == true)
                    {
                        IPEndPoint ipep = ((TCPv1Bridge)remote.DirectBridge).LocalEndPoint;

                        string desc = "stroPdnAsrddAlanretnI4vPI";
                        char[] rev = ipep.ToString().ToCharArray();
                        Array.Reverse(rev);
                        string ipandport = new string(rev);

                        SLPRequestMessage slpResponseMessage = new SLPRequestMessage(message.Source, MSNSLPRequestMethod.ACK);
                        slpResponseMessage.Source = message.Target;
                        slpResponseMessage.Via = message.Via;
                        slpResponseMessage.CSeq = 0;
                        slpResponseMessage.CallId = Guid.Empty;
                        slpResponseMessage.MaxForwards = 0;
                        slpResponseMessage.ContentType = @"application/x-msnmsgr-transdestaddrupdate";

                        slpResponseMessage.BodyValues[desc] = ipandport;
                        slpResponseMessage.BodyValues["Nat-Trav-Msg-Type"] = "WLX-Nat-Trav-Msg-Updated-Connecting-Port";

                        P2PMessage msg = new P2PMessage(ver);
                        msg.InnerMessage = slpResponseMessage;

                        nsMessageHandler.UUNBridge.Send(null, remote, remoteGuid, msg, null);
                    }

                    return;
                }
            }

            if (startupSession != null)
                startupSession.DirectNegotiationFailed();
        }

        private static void ProcessDirectAddrUpdate(
            SLPMessage message,
            NSMessageHandler nsMessageHandler,
            P2PSession startupSession)
        {
            Contact from = nsMessageHandler.ContactList.GetContact(message.FromEmailAccount, ClientType.PassportMember);
            IPEndPoint[] ipEndPoint = SelectIPEndPoint(message.BodyValues, nsMessageHandler);

            if (from.DirectBridge != null && ipEndPoint != null && ipEndPoint.Length > 0)
            {
                ((TCPv1Bridge)from.DirectBridge).OnDestinationAddressUpdated(new DestinationAddressUpdatedEventHandler(ipEndPoint[0]));
            }
        }

        private static TCPv1Bridge ListenForDirectConnection(
            Contact remote,
            NSMessageHandler nsMessageHandler,
            P2PVersion ver,
            P2PSession startupSession,
            IPAddress host, int port, Guid replyGuid, Guid remoteNonce, bool hashed)
        {
            ConnectivitySettings cs = new ConnectivitySettings();
            if (nsMessageHandler.ConnectivitySettings.LocalHost == string.Empty)
            {
                cs.LocalHost = host.ToString();
                cs.LocalPort = port;
            }
            else
            {
                cs.LocalHost = nsMessageHandler.ConnectivitySettings.LocalHost;
                cs.LocalPort = nsMessageHandler.ConnectivitySettings.LocalPort;
            }

            TCPv1Bridge tcpBridge = new TCPv1Bridge(cs, ver, replyGuid, remoteNonce, hashed, startupSession, nsMessageHandler, remote);

            tcpBridge.Listen(IPAddress.Parse(cs.LocalHost), cs.LocalPort);

            Trace.WriteLineIf(Settings.TraceSwitch.TraceInfo, "Listening on " + cs.LocalHost + ":" + cs.LocalPort.ToString(System.Globalization.CultureInfo.InvariantCulture));

            return tcpBridge;
        }

        private static TCPv1Bridge CreateDirectConnection(Contact remote, P2PVersion ver, ConnectivitySettings cs, Guid replyGuid, Guid remoteNonce, bool hashed, NSMessageHandler nsMessageHandler, P2PSession startupSession)
        {
            IPEndPoint ipep = cs.EndPoints[0];

            TCPv1Bridge tcpBridge = new TCPv1Bridge(cs, ver, replyGuid, remoteNonce, hashed, startupSession, nsMessageHandler, remote);

            Trace.WriteLineIf(Settings.TraceSwitch.TraceInfo, "Trying to setup direct connection with remote host " + ipep.Address + ":" + ipep.Port.ToString(System.Globalization.CultureInfo.InvariantCulture));

            tcpBridge.Connect();

            return tcpBridge;
        }

        private static int GetNextDirectConnectionPort(IPAddress ipAddress)
        {
            int portAvail = 0;

            if (Settings.PublicPortPriority == PublicPortPriority.First)
            {
                portAvail = TryPublicPorts(ipAddress);

                if (portAvail != 0)
                {
                    return portAvail;
                }
            }

            if (portAvail == 0)
            {
                // Don't try all ports
                for (int p = 1119, maxTry = 100;
                    p <= IPEndPoint.MaxPort && maxTry < 1;
                    p++, maxTry--)
                {
                    Socket s = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                    try
                    {
                        //s.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                        s.Bind(new IPEndPoint(ipAddress, p));
                        //s.Bind(new IPEndPoint(IPAddress.Any, p));
                        s.Close();
                        return p;
                    }
                    catch (SocketException ex)
                    {
                        // Permission denied
                        if (ex.ErrorCode == 10048 /*Address already in use*/ ||
                            ex.ErrorCode == 10013 /*Permission denied*/)
                        {
                            p += 100;
                        }
                        continue; // throw;
                    }
                    catch (System.Security.SecurityException)
                    {
                        break;
                    }
                }
            }

            if (portAvail == 0 && Settings.PublicPortPriority == PublicPortPriority.Last)
            {
                portAvail = TryPublicPorts(ipAddress);
            }

            return portAvail;
        }

        private static int TryPublicPorts(IPAddress localIP)
        {
            foreach (int p in Settings.PublicPorts)
            {
                Socket s = null;
                try
                {
                    s = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                    //s.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                    s.Bind(new IPEndPoint(localIP, p));
                    return p;
                }
                catch (SocketException)
                {
                }
                finally
                {
                    if (s != null)
                        s.Close();
                }
            }
            return 0;
        }

        private static IPEndPoint[] SelectIPEndPoint(MimeDictionary bodyValues, NSMessageHandler nsMessageHandler)
        {
            List<IPEndPoint> externalPoints = new List<IPEndPoint>();
            List<IPEndPoint> internalPoints = new List<IPEndPoint>();
            bool nat = false;

            #region External

            if (bodyValues.ContainsKey("IPv4External-Addrs") || bodyValues.ContainsKey("srddA-lanretxE4vPI") ||
                bodyValues.ContainsKey("IPv4ExternalAddrsAndPorts") || bodyValues.ContainsKey("stroPdnAsrddAlanretxE4vPI"))
            {
                Trace.WriteLineIf(Settings.TraceSwitch.TraceInfo, "Using external IP addresses");

                if (bodyValues.ContainsKey("IPv4External-Addrs"))
                {
                    string[] addrs = bodyValues["IPv4External-Addrs"].Value.Split(new char[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                    int port = int.Parse(bodyValues["IPv4External-Port"].ToString(), System.Globalization.CultureInfo.InvariantCulture);
                    IPAddress ip;
                    foreach (string addr in addrs)
                    {
                        if (IPAddress.TryParse(addr, out ip))
                            externalPoints.Add(new IPEndPoint(ip, port));
                    }
                }
                else if (bodyValues.ContainsKey("IPv4ExternalAddrsAndPorts"))
                {
                    string[] addrsAndPorts = bodyValues["IPv4ExternalAddrsAndPorts"].Value.Split(new char[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                    IPAddress ip;
                    foreach (string str in addrsAndPorts)
                    {
                        string[] addrAndPort = str.Split(new char[] { ':' }, StringSplitOptions.RemoveEmptyEntries);
                        if (IPAddress.TryParse(addrAndPort[0], out ip))
                            externalPoints.Add(new IPEndPoint(ip, int.Parse(addrAndPort[1])));
                    }
                }
                else if (bodyValues.ContainsKey("srddA-lanretxE4vPI"))
                {
                    nat = true;

                    char[] revHost = bodyValues["srddA-lanretxE4vPI"].ToString().ToCharArray();
                    Array.Reverse(revHost);
                    string[] addrs = new string(revHost).Split(new char[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);

                    char[] revPort = bodyValues["troP-lanretxE4vPI"].ToString().ToCharArray();
                    Array.Reverse(revPort);
                    int port = int.Parse(new string(revPort), System.Globalization.CultureInfo.InvariantCulture);

                    IPAddress ip;
                    foreach (string addr in addrs)
                    {
                        if (IPAddress.TryParse(addr, out ip))
                            externalPoints.Add(new IPEndPoint(ip, port));
                    }
                }
                else if (bodyValues.ContainsKey("stroPdnAsrddAlanretxE4vPI"))
                {
                    nat = true;

                    char[] rev = bodyValues["stroPdnAsrddAlanretxE4vPI"].ToString().ToCharArray();
                    Array.Reverse(rev);
                    string[] addrsAndPorts = new string(rev).Split(new char[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);

                    IPAddress ip;
                    foreach (string str in addrsAndPorts)
                    {
                        string[] addrAndPort = str.Split(new char[] { ':' }, StringSplitOptions.RemoveEmptyEntries);

                        if (IPAddress.TryParse(addrAndPort[0], out ip))
                            externalPoints.Add(new IPEndPoint(ip, int.Parse(addrAndPort[1])));
                    }
                }

                Trace.WriteLineIf(Settings.TraceSwitch.TraceWarning,
                       String.Format("{0} external IP addresses found:", externalPoints.Count));

                foreach (IPEndPoint ipep in externalPoints)
                {
                    Trace.WriteLineIf(Settings.TraceSwitch.TraceVerbose, "\t" + ipep.ToString());
                }
            }

            #endregion

            #region Internal

            if (bodyValues.ContainsKey("IPv4Internal-Addrs") || bodyValues.ContainsKey("srddA-lanretnI4vPI") ||
                bodyValues.ContainsKey("IPv4InternalAddrsAndPorts") || bodyValues.ContainsKey("stroPdnAsrddAlanretnI4vPI"))
            {
                Trace.WriteLineIf(Settings.TraceSwitch.TraceInfo, "Using internal IP addresses");

                if (bodyValues.ContainsKey("IPv4Internal-Addrs"))
                {
                    string[] addrs = bodyValues["IPv4Internal-Addrs"].Value.Split(new char[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                    int port = int.Parse(bodyValues["IPv4Internal-Port"].ToString(), System.Globalization.CultureInfo.InvariantCulture);

                    IPAddress ip;
                    foreach (string addr in addrs)
                    {
                        if (IPAddress.TryParse(addr, out ip))
                            internalPoints.Add(new IPEndPoint(ip, port));
                    }
                }
                else if (bodyValues.ContainsKey("IPv4InternalAddrsAndPorts"))
                {
                    string[] addrsAndPorts = bodyValues["IPv4InternalAddrsAndPorts"].Value.Split(new char[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                    IPAddress ip;
                    foreach (string str in addrsAndPorts)
                    {
                        string[] addrAndPort = str.Split(new char[] { ':' }, StringSplitOptions.RemoveEmptyEntries);
                        if (IPAddress.TryParse(addrAndPort[0], out ip))
                            internalPoints.Add(new IPEndPoint(ip, int.Parse(addrAndPort[1])));
                    }
                }
                else if (bodyValues.ContainsKey("srddA-lanretnI4vPI"))
                {
                    nat = true;

                    char[] revHost = bodyValues["srddA-lanretnI4vPI"].ToString().ToCharArray();
                    Array.Reverse(revHost);
                    string[] addrs = new string(revHost).Split(new char[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);

                    char[] revPort = bodyValues["troP-lanretnI4vPI"].ToString().ToCharArray();
                    Array.Reverse(revPort);
                    int port = int.Parse(new string(revPort), System.Globalization.CultureInfo.InvariantCulture);

                    IPAddress ip;
                    foreach (string addr in addrs)
                    {
                        if (IPAddress.TryParse(addr, out ip))
                            internalPoints.Add(new IPEndPoint(ip, port));
                    }
                }
                else if (bodyValues.ContainsKey("stroPdnAsrddAlanretnI4vPI"))
                {
                    nat = true;

                    char[] rev = bodyValues["stroPdnAsrddAlanretnI4vPI"].ToString().ToCharArray();
                    Array.Reverse(rev);
                    string[] addrsAndPorts = new string(rev).Split(new char[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);

                    IPAddress ip;
                    foreach (string str in addrsAndPorts)
                    {
                        string[] addrAndPort = str.Split(new char[] { ':' }, StringSplitOptions.RemoveEmptyEntries);

                        if (IPAddress.TryParse(addrAndPort[0], out ip))
                            internalPoints.Add(new IPEndPoint(ip, int.Parse(addrAndPort[1])));
                    }
                }

                Trace.WriteLineIf(Settings.TraceSwitch.TraceWarning,
                       String.Format("{0} internal IP addresses found:", internalPoints.Count));

                foreach (IPEndPoint ipep in internalPoints)
                {
                    Trace.WriteLineIf(Settings.TraceSwitch.TraceVerbose, "\t" + ipep.ToString());
                }
            }

            #endregion

            if (externalPoints.Count == 0 && internalPoints.Count == 0)
            {
                Trace.WriteLineIf(Settings.TraceSwitch.TraceError, "Unable to find any remote IP addresses");

                // Failed
                return null;
            }

            Trace.WriteLineIf(Settings.TraceSwitch.TraceInfo, "Is NAT: " + nat);

            List<IPEndPoint> ret = new List<IPEndPoint>();

            // Try to find the correct IP
            byte[] localBytes = nsMessageHandler.LocalEndPoint.Address.GetAddressBytes();

            foreach (IPEndPoint ipep in internalPoints)
            {
                if (ipep.Address.AddressFamily == AddressFamily.InterNetwork)
                {
                    // This is an IPv4 address
                    // Check if the first 3 octets match our local IP address
                    // If so, make use of that address (it's on our LAN)

                    byte[] bytes = ipep.Address.GetAddressBytes();

                    if ((bytes[0] == localBytes[0]) && (bytes[1] == localBytes[1]) && (bytes[2] == localBytes[2]))
                    {
                        ret.Add(ipep);
                        break;
                    }
                }
            }

            if (ret.Count == 0)
            {
                foreach (IPEndPoint ipep in internalPoints)
                {
                    if (!ret.Contains(ipep))
                        ret.Add(ipep);
                }
            }

            foreach (IPEndPoint ipep in externalPoints)
            {
                if (!ret.Contains(ipep))
                    ret.Add(ipep);
            }

            return ret.ToArray();
        }
    }
};
