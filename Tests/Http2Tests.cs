﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Org.Mentalis;
using Org.Mentalis.Security.Ssl;
using Org.Mentalis.Security.Ssl.Shared.Extensions;
using Owin.Types;
using SharedProtocol;
using SharedProtocol.Framing;
using SharedProtocol.Handshake;
using SocketServer;
using Xunit;
using Xunit.Extensions;
using System.Configuration;

namespace Http2Tests
{
    public class Http2Tests
    {
        private HttpSocketServer _http2Server;

        private async Task InvokeMiddleWare(IDictionary<string, object> environment)
        {
            var handshakeAction = (Action)environment["HandshakeAction"];
            handshakeAction.Invoke();
        }

        public Http2Tests()
        {
            const string address = @"http://localhost:8443/";
            Uri uri;
            Uri.TryCreate(address, UriKind.Absolute, out uri);

            var properties = new Dictionary<string, object>();
            var addresses = new List<IDictionary<string, object>>()
                {
                    new Dictionary<string, object>()
                        {
                            {"host", uri.Host},
                            {"scheme", uri.Scheme},
                            {"port", uri.Port.ToString()},
                            {"path", uri.AbsolutePath}
                        }
                };

            properties.Add(OwinConstants.CommonKeys.Addresses, addresses);

            var assemblyPath = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
            var rootPath = @"\root";
            var serverRootDir = assemblyPath + rootPath;
            var serverRootFile = serverRootDir + @"/test.txt";
            Directory.CreateDirectory(serverRootDir);
            var content = Encoding.UTF8.GetBytes("HelloWorld"); //10 bytes

            using (var stream = new FileStream(serverRootFile, FileMode.Create))
            {
                //Write 10 000 000 bytes or 10 mb
                for (int i = 0; i < 1000000; i++)
                {
                    stream.Write(content, 0, content.Length);
                }
            }

            new Thread((ThreadStart)delegate
                {
                    _http2Server = new HttpSocketServer(InvokeMiddleWare, properties);
                }).Start();
        }

        private static SecureSocket GetHandshakedSocket(Uri uri)
        {
            string selectedProtocol = null;

            var extensions = new [] { ExtensionType.Renegotiation, ExtensionType.ALPN };

            var options = new SecurityOptions(SecureProtocol.Tls1, extensions, ConnectionEnd.Client);

            options.VerificationType = CredentialVerification.None;
            options.Certificate = Org.Mentalis.Security.Certificates.Certificate.CreateFromCerFile(@"certificate.pfx");
            options.Flags = SecurityFlags.Default;
            options.AllowedAlgorithms = SslAlgorithms.RSA_AES_128_SHA | SslAlgorithms.NULL_COMPRESSION;

            var sessionSocket = new SecureSocket(AddressFamily.InterNetwork, SocketType.Stream,
                                                ProtocolType.Tcp, options);

            using (var monitor = new ALPNExtensionMonitor())
            {
                monitor.OnProtocolSelected += (sender, args) => { selectedProtocol = args.SelectedProtocol; };

                sessionSocket.Connect(new DnsEndPoint(uri.Host, uri.Port), monitor);

                HandshakeManager.GetHandshakeAction(sessionSocket, options).Invoke();
            }

            return sessionSocket;
        }

        private static Http2Stream SubmitRequest(Http2Session session, Uri uri)
        {
            var pairs = new Dictionary<string, string>(10);
            const string method = "GET";
            string path = uri.PathAndQuery;
            const string version = "HTTP/2.0";
            string scheme = uri.Scheme;
            string host = uri.Host;

            pairs.Add(":method", method);
            pairs.Add(":path", path);
            pairs.Add(":version", version);
            pairs.Add(":host", host);
            pairs.Add(":scheme", scheme);
            
            var clientStream = session.SendRequest(pairs, 3, true);

            return clientStream;
        }
   
        [Fact]
        public void StartSessionAndSendRequestSuccessful()
        {
            const string requestStr = @"http://localhost:8443/test.txt";
            Uri uri;
            Uri.TryCreate(requestStr, UriKind.Absolute, out uri);
            
            bool wasSettingsSent = false;
            bool wasHeadersPlusPrioritySent = false;
            bool wasSocketClosed = false;

            var settingsSentRaisedEventArgs = new ManualResetEvent(false);
            var headersPlusPriSentRaisedEvent = new ManualResetEvent(false);
            var socketClosedRaisedEvent = new ManualResetEvent(false);

            var socket = GetHandshakedSocket(uri);

            socket.OnClose += (sender, args) =>
                {
                    socketClosedRaisedEvent.Set();
                    wasSocketClosed = true;
                };

            var session = new Http2Session(socket, ConnectionEnd.Client);

            session.OnSettingsSent += (o, args) =>
            {
                wasSettingsSent = true;

                Assert.Equal(args.SettingsFrame.StreamId, 0);

                settingsSentRaisedEventArgs.Set();
            };

            session.OnFrameSent += (sender, args) =>
            {
                if (wasHeadersPlusPrioritySent == false)
                {
                    wasHeadersPlusPrioritySent = args.Frame is HeadersPlusPriority;

                    headersPlusPriSentRaisedEvent.Set();
                }
            };

            session.Start();

            settingsSentRaisedEventArgs.WaitOne(60000);

            var stream = SubmitRequest(session, uri);

            headersPlusPriSentRaisedEvent.WaitOne(60000);
            
            //Settings frame does not contain flow control settings in this test. 
            Assert.Equal(session.ActiveStreams.Count, 1);
            Assert.Equal(session.ActiveStreams.FlowControlledStreams.Count, 1);
            Assert.Equal(stream.IsFlowControlBlocked, false);
            Assert.Equal(stream.Id, 1);
            Assert.Equal(stream.IsFlowControlEnabled, true);
            Assert.Equal(stream.FinSent, true);
            Assert.Equal(stream.Disposed, false);
            Assert.Equal(wasHeadersPlusPrioritySent, true);
            Assert.Equal(wasSettingsSent, true);

            headersPlusPriSentRaisedEvent.Dispose();
            settingsSentRaisedEventArgs.Dispose();
            session.Dispose();

            socketClosedRaisedEvent.WaitOne(60000);

            Assert.Equal(wasSocketClosed, true);
        }

        [Fact]
        public void StartMultipleSessionAndSendMultipleRequests()
        {
            for (int i = 0; i < 1000; i++)
            {
                StartSessionAndSendRequestSuccessful();
            }
        }

        [Fact]
        public void StartSessionAndGet10mbDataSuccessful()
        {
            const string requestStr = @"http://localhost:8443/test.txt";
            Uri uri;
            Uri.TryCreate(requestStr, UriKind.Absolute, out uri);

            bool wasSettingsSent = false;
            bool wasHeadersPlusPrioritySent = false;
            bool wasSocketClosed = false;

            var socketClosedRaisedEvent = new ManualResetEvent(false);

            var socket = GetHandshakedSocket(uri);

            socket.OnClose += (sender, args) =>
            {
                socketClosedRaisedEvent.Set();
                wasSocketClosed = true;
            };

            var session = new Http2Session(socket, ConnectionEnd.Client);

            session.OnSettingsSent += (o, args) =>
            {
                wasSettingsSent = true;

                Assert.Equal(args.SettingsFrame.StreamId, 0);
            };

            session.OnFrameSent += (sender, args) =>
            {
                if (wasHeadersPlusPrioritySent == false)
                {
                    wasHeadersPlusPrioritySent = args.Frame is HeadersPlusPriority;
                }
            };

            session.Start();

            var stream = SubmitRequest(session, uri);



            session.Dispose();

            socketClosedRaisedEvent.WaitOne(60000);

            Assert.Equal(wasSocketClosed, true);
        }

        ~Http2Tests()
        {
            _http2Server.Dispose();
        }
    }
}
