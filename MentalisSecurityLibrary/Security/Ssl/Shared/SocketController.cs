//-----------------------------------------------------------------------
// <copyright file="SocketController.cs" company="Microsoft Open Technologies, Inc.">
//Copyright � 2002-2007, The Mentalis.org Team
//Portions Copyright � Microsoft Open Technologies, Inc.
//All rights reserved.
//http://www.mentalis.org/ 
//Redistribution and use in source and binary forms, with or without modification, 
//are permitted provided that the following conditions are met:
//- Redistributions of source code must retain the above copyright notice, this list of conditions and the following disclaimer. 
//- Neither the name of the Mentalis.org Team, 
//nor the names of its contributors may be used to endorse or promote products derived from this software without specific prior written permission.
//THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS "AS IS" AND ANY EXPRESS OR IMPLIED WARRANTIES, 
//INCLUDING, BUT NOT LIMITED TO, 
//THE IMPLIED WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE ARE DISCLAIMED. 
//IN NO EVENT SHALL THE COPYRIGHT OWNER OR CONTRIBUTORS BE LIABLE FOR ANY DIRECT, 
//INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR CONSEQUENTIAL DAMAGES (INCLUDING, BUT NOT LIMITED TO, 
//PROCUREMENT OF SUBSTITUTE GOODS OR SERVICES; LOSS OF USE, DATA, OR PROFITS; 
//OR BUSINESS INTERRUPTION) HOWEVER CAUSED AND ON ANY THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY,
//OR TORT (INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE OF THIS SOFTWARE, 
//EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.
// </copyright>
//-----------------------------------------------------------------------

/*
 *   Mentalis.org Security Library
 * 
 *     Copyright � 2002-2005, The Mentalis.org Team
 *     All rights reserved.
 *     http://www.mentalis.org/
 *
 *
 *   Redistribution and use in source and binary forms, with or without
 *   modification, are permitted provided that the following conditions
 *   are met:
 *
 *     - Redistributions of source code must retain the above copyright
 *        notice, this list of conditions and the following disclaimer. 
 *
 *     - Neither the name of the Mentalis.org Team, nor the names of its contributors
 *        may be used to endorse or promote products derived from this
 *        software without specific prior written permission. 
 *
 *   THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS
 *   "AS IS" AND ANY EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT
 *   LIMITED TO, THE IMPLIED WARRANTIES OF MERCHANTABILITY AND FITNESS
 *   FOR A PARTICULAR PURPOSE ARE DISCLAIMED. IN NO EVENT SHALL
 *   THE COPYRIGHT OWNER OR CONTRIBUTORS BE LIABLE FOR ANY DIRECT,
 *   INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR CONSEQUENTIAL DAMAGES
 *   (INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR
 *   SERVICES; LOSS OF USE, DATA, OR PROFITS; OR BUSINESS INTERRUPTION)
 *   HOWEVER CAUSED AND ON ANY THEORY OF LIABILITY, WHETHER IN CONTRACT,
 *   STRICT LIABILITY, OR TORT (INCLUDING NEGLIGENCE OR OTHERWISE)
 *   ARISING IN ANY WAY OUT OF THE USE OF THIS SOFTWARE, EVEN IF ADVISED
 *   OF THE POSSIBILITY OF SUCH DAMAGE.
*/

using System;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Collections;
using System.IO;
using System.Security.Cryptography;
using Org.Mentalis.Security.Certificates;
using Org.Mentalis.Security.Cryptography;
using Org.Mentalis.Security.Ssl.Shared;
using Org.Mentalis.Security.Ssl.Ssl3;
using Org.Mentalis.Security.Ssl.Tls1;
using Org.Mentalis.Security.Ssl.Shared.Extensions;

namespace Org.Mentalis.Security.Ssl.Shared {
	internal class SocketController : IDisposable {
		public SocketController(SecureSocket parent, Socket socket, SecurityOptions options) {
            this.m_Parent = parent;
            this.m_Socket = socket;
            this.m_IsDisposed = false;
            this.m_ActiveSend = null;
            this.m_ActiveReceive = null;
            this.m_DecryptedBuffer = new XBuffer();
            this.m_ToSendList = new ArrayList(2);
            this.m_SentList = new ArrayList(2);
            this.m_ReceiveBuffer = new byte[m_ReceiveBufferLength];

            this.OnConnectionClose += new EventHandler<SocketCloseEventArgs>(parent.ConnectionCloseHandler);

			this.m_Compatibility = new CompatibilityLayer(this, options);

            try 
            {
				m_Socket.BeginReceive(m_ReceiveBuffer, 0, m_ReceiveBufferLength, SocketFlags.None, new AsyncCallback(this.OnReceive), null);
			} 
            catch (Exception e) 
            {
				CloseConnection(e);
			}
			if (options.Entity == ConnectionEnd.Client) 
            {
				//				byte[] hello = m_RecordLayer.GetControlBytes(ControlType.ClientHello);
				byte[] hello = m_Compatibility.GetClientHello();
				BeginSend(hello, 0, hello.Length, null, DataType.ProtocolData);
			}
		}

        public event EventHandler<SocketCloseEventArgs> OnConnectionClose;

		protected void OnReceive(IAsyncResult ar) {
			lock(this) {	// synchronize
				try {
					//MsgMe("OnReceive", "client calls EndReceive()");
					int size = m_Socket.EndReceive(ar);
					if (size == 0) {
						CloseConnection(null); // connection has been shut down
					} else {
						//MsgMe("OnReceive", "client got from socket", size);
						SslRecordStatus status;
						if (m_RecordLayer == null) {
							//MsgMe("OnReceive", "client calls ProcessHello()", true, m_ReceiveBuffer, size);
							CompatibilityResult ret = m_Compatibility.ProcessHello(m_ReceiveBuffer, 0, size);
							m_RecordLayer = ret.RecordLayer;
							status = ret.Status;
							if (m_RecordLayer != null)
							{
								//MsgMe("OnReceive", "client sets m_Compatibility == null because m_RecordLayer != null");
								//m_Compatibility = null;
							}
						} else {
							//MsgMe("OnReceive", "client calls ProcessBytes()");
							status = m_RecordLayer.ProcessBytes(m_ReceiveBuffer, 0, size);
						}
						if (status.Buffer != null) {
							if (status.Status == SslStatus.Close) { // shut down the connection after the send
								//MsgMe("OnReceive", "client sets calls m_IsShuttingDown = true because Status == SslStatus.Close");
								m_IsShuttingDown = true;
							}
							//MsgMe("OnReceive", "client calls BeginSend()");
							BeginSend(status.Buffer, 0, status.Buffer.Length, null, DataType.ProtocolData);
						} else if (status.Status == SslStatus.Close) { // Record Layer instructs us to shut down
							m_Socket.Shutdown(SocketShutdown.Both);
							//MsgMe("OnReceive", "client calls CloseConnection() because Status == SslStatus.Close");
							CloseConnection(null);
						} else if (status.Status == SslStatus.OK) {
							//MsgMe("OnReceive", "client calls ResumeSending()");
							ResumeSending();
						}
						if (status.Decrypted != null)
							ProcessDecryptedBytes(status.Decrypted);
						if (!m_IsDisposed && !m_IsShuttingDown)
							//MsgMe("OnReceive", "client calls BeginReceive()");
							m_Socket.BeginReceive(m_ReceiveBuffer, 0, m_ReceiveBufferLength, SocketFlags.None, new AsyncCallback(this.OnReceive), null);
					}
				} catch (Exception e) {
					CloseConnection(e);
				}
			}
		}
		protected void OnSent(IAsyncResult ar) {
			lock(this) {	// synchronize
				try {
					if (!m_IsDisposed) {
						int sent = m_Socket.EndSend(ar);
						m_ActiveSend.Transferred += sent;
						//MsgMe("OnSent", "client called Socket.EndSend()", sent);
						if (m_ActiveSend.Transferred != m_ActiveSend.Size)
						{
							//MsgMe("OnSent", "client calls BeginSend() because Transferred != Size");
							m_Socket.BeginSend(m_ActiveSend.Buffer, m_ActiveSend.Offset + m_ActiveSend.Transferred, m_ActiveSend.Size - m_ActiveSend.Transferred, SocketFlags.None, new AsyncCallback(this.OnSent), null);
						} else {
							//MsgMe("OnSent", "client sets m_IsSending = false");
							m_IsSending = false;
							if (m_ActiveSend.AsyncResult != null) {
								m_SentList.Add(m_ActiveSend);
								m_ActiveSend.AsyncResult.Notify(null);
							}
							if (m_IsShuttingDown && (m_ToSendList.Count == 0 || ((TransferItem)m_ToSendList[0]).Type == DataType.ApplicationData)) {
								//MsgMe("OnSent", "client calls CloseConnection() because m_IsShuttingDown");
								CloseConnection(null);
							} else {
								//MsgMe("OnSent", "client calls ResumeSending()");
								ResumeSending();
							}
						}
					}
				} catch (Exception e) {
					CloseConnection(e);
				}
			}
		}
		public AsyncResult BeginSend(byte[] buffer, int offset, int size, AsyncCallback callback, object state) {
			//MsgMe("BeginSend(S)", "client sending buffer", true, buffer);
			lock(this) {	// synchronize
				if (m_IsDisposed)
					throw new ObjectDisposedException(this.GetType().FullName);
				if (m_IsShuttingDown)
					throw new SocketException();
				return BeginSend(buffer, offset, size, new AsyncResult(callback, state, this), DataType.ApplicationData);
			}
		}
		protected AsyncResult BeginSend(byte[] buffer, int offset, int size, AsyncResult asyncResult, DataType type) { // not synced!
			//MsgMe("BeginSend(AS)", "client sending buffer");
			int position = m_ToSendList.Count;
            if (type == DataType.ProtocolData)
            {
                position = 0;
            }
            else
            {
                if (!IsNegotiationCompleted)
                    throw new SslException(AlertDescription.InternalError, "Attempt to send data before negotiation is complete");
            }

			TransferItem item = new TransferItem(buffer, offset, size, asyncResult, type);
			m_ToSendList.Insert(position, item);
			ResumeSending();
			//MsgMe("BeginSend(AS)", " client returns");
			return item.AsyncResult;
		}
		protected int FindIndex(IAsyncResult ar, ArrayList list) {
			for(int i = 0; i < list.Count; i++) {
				if (((TransferItem)list[i]).AsyncResult == ar) {
					return i;
				}
			}
			return -1;
		}
		public TransferItem EndSend(IAsyncResult ar) { // returns null if the specified IAsyncResult is not ours
			TransferItem ret;
			lock(this) {	// synchronize
				int index = FindIndex(ar, m_SentList);
				if (index < 0) {
					if (m_ActiveSend != null && m_ActiveSend.AsyncResult == ar) {
						ret = m_ActiveSend;
					} else {
						index = FindIndex(ar, m_ToSendList);
						if (index < 0)
							return null;
						ret = (TransferItem)m_ToSendList[index];
					}
				} else {
					ret = (TransferItem)m_SentList[index];
				}
			}
			// do _not_ call this method inside the critical section, or the code may deadlock!
			while (!ret.AsyncResult.IsCompleted) {
				ret.AsyncResult.AsyncWaitHandle.WaitOne(200, false);
			}
			lock(this) {
				m_SentList.Remove(ret);
			}
			return ret;
		}
		// Thanks go out to Kevin Knoop for optimizing this method
		protected void ProcessDecryptedBytes(byte[] buffer) { // not synced!
			if (buffer != null) {
				m_DecryptedBuffer.Seek(0,SeekOrigin.End);
				m_DecryptedBuffer.Write(buffer,0,buffer.Length);
			}
			if (m_ActiveReceive != null && m_ActiveReceive.Transferred == 0 && (m_ActiveReceive.AsyncResult == null || !m_ActiveReceive.AsyncResult.IsCompleted)) {
				if (m_DecryptedBuffer.Length > m_ActiveReceive.Size) {
					m_DecryptedBuffer.Seek(0,SeekOrigin.Begin);
					m_DecryptedBuffer.Read(m_ActiveReceive.Buffer,m_ActiveReceive.Offset,m_ActiveReceive.Size);
					m_DecryptedBuffer.RemoveXBytes(m_ActiveReceive.Size);
					m_ActiveReceive.Transferred = m_ActiveReceive.Size;
				} else {
					m_DecryptedBuffer.Seek(0,SeekOrigin.Begin);
					m_DecryptedBuffer.Read(m_ActiveReceive.Buffer,m_ActiveReceive.Offset,(int)m_DecryptedBuffer.Length);
					m_ActiveReceive.Transferred = (int)m_DecryptedBuffer.Length;
					m_DecryptedBuffer.SetLength(0);
				}
				if (m_ActiveReceive.AsyncResult != null)
					m_ActiveReceive.AsyncResult.Notify(null);
			}
		}

		public int Available {
			get {
				lock(this) {
					return (int)m_DecryptedBuffer.Length;
				}
			}
		}
		public AsyncResult BeginReceive(byte[] buffer, int offset, int size, AsyncCallback callback, object state) {
			lock(this) {
				if (m_ActiveReceive != null)
					throw new SocketException();
				AsyncResult ret = new AsyncResult(callback, state, this);
				m_ActiveReceive = new TransferItem(buffer, offset, size, ret, DataType.ApplicationData);
				if (m_DecryptedBuffer.Length > 0) {
					ProcessDecryptedBytes(null);
				} else {
					if (!m_Socket.Connected && m_ActiveReceive.AsyncResult != null)
						m_ActiveReceive.AsyncResult.Notify(null);
				}
				return ret;
			}
		}
		public TransferItem EndReceive(IAsyncResult ar) { // returns null if the specified IAsyncResult is not ours
			TransferItem ret;
			lock(this) {	// synchronize
				if (ar != m_ActiveReceive.AsyncResult) {
					return null;
				} else {
					ret = m_ActiveReceive;
				}
			}
            // do _not_ call this method inside the critical section, or the code may deadlock!
            while (!ret.AsyncResult.IsCompleted) {
                ret.AsyncResult.AsyncWaitHandle.WaitOne(200, false);
            }
			lock(this) {
				m_ActiveReceive = null;
			}
			return ret;
		}
		protected byte[] AppendBytes(byte[] buffer1, int offset1, int size1, byte[] buffer2, int offset2, int size2) { // not synced!
			byte[] ret = new byte[size1 + size2];
			Buffer.BlockCopy(buffer1, offset1, ret, 0, size1);
			Buffer.BlockCopy(buffer2, offset2, ret, size1, size2);
			return ret;
		}
		protected byte[] SplitBytes(ref byte[] buffer, int maxReturnLength) {
			if (buffer.Length < maxReturnLength)
				maxReturnLength = buffer.Length;
			byte[] ret = new byte[maxReturnLength];
			Buffer.BlockCopy(buffer, 0, ret, 0, maxReturnLength);
			byte[] newBuf = new byte[buffer.Length - maxReturnLength];
			if (newBuf.Length > 0)
				Buffer.BlockCopy(buffer, maxReturnLength, newBuf, 0, newBuf.Length);
			buffer = newBuf;
			return ret;
		}
		protected void ResumeSending() { // not synced!
			if (m_IsSending) // if we're already sending, return
			{
				return;
			}
			if (m_ToSendList.Count == 0) // is there anything to send?
			{
				return;
			}
			// we should not send application data if a negotiation is in progress
			if ((m_RecordLayer == null && ((TransferItem)m_ToSendList[0]).Type == DataType.ApplicationData) || (m_RecordLayer != null && m_RecordLayer.IsNegotiating() && ((TransferItem)m_ToSendList[0]).Type == DataType.ApplicationData))
			{
				return;
			}
			m_ActiveSend = (TransferItem)m_ToSendList[0];
			m_ToSendList.RemoveAt(0);
			m_IsSending = true;
			try {
				// if we're sending application data, encrypt it before sending
				// protocol data should not be touched
				if (m_ActiveSend.Type == DataType.ApplicationData) {
					m_ActiveSend.Buffer = m_RecordLayer.EncryptBytes(m_ActiveSend.Buffer, m_ActiveSend.Offset, m_ActiveSend.Size, ContentType.ApplicationData);
					m_ActiveSend.Offset = 0;
					m_ActiveSend.Size = m_ActiveSend.Buffer.Length;
				}
				//MsgMe("ResumeSending", "client calls Socket.BeginSend", m_ActiveSend);
				m_Socket.BeginSend(m_ActiveSend.Buffer, m_ActiveSend.Offset, m_ActiveSend.Size, SocketFlags.None, new AsyncCallback(this.OnSent), null);
			} catch (Exception e) {
				CloseConnection(e);
			}
		}
		public void Dispose() {
			lock(this) {	// synchronize
				CloseConnection(null);
			}
		}
		protected void CloseConnection(Exception e) { // not synced!
			// e == SocketException || SslException
			if (!m_IsDisposed) {

                if (this.OnConnectionClose != null)
					this.OnConnectionClose(this, new SocketCloseEventArgs() { Exception=e });

				m_IsDisposed = true;
				try {
					m_Socket.Shutdown(SocketShutdown.Both);
				} catch {}
				m_Socket.Close();
				if (m_ActiveSend != null) {
					if (m_ActiveSend.AsyncResult != null) {
						m_SentList.Add(m_ActiveSend);
						m_ActiveSend.AsyncResult.Notify(e);
					}
				}
				Exception f = e;
				if (f == null)
					f = new SslException(AlertDescription.UnexpectedMessage, "The bytes could not be sent because the connection has been closed.");
				for(int i = 0; i < m_ToSendList.Count; i++) {
					m_ActiveSend = (TransferItem)m_ToSendList[i];
					m_SentList.Add(m_ActiveSend);
					m_ActiveSend.AsyncResult.Notify(f);
				}
				m_ToSendList.Clear();
				if (m_ActiveReceive != null && m_ActiveReceive.AsyncResult != null) {
					m_ActiveReceive.AsyncResult.Notify(e);
				}
				if (m_ShutdownCallback != null) {
					m_ShutdownCallback.Notify(e);
				}
				// destroy sensitive data
				if (m_RecordLayer != null)
					m_RecordLayer.Dispose();
			}
		}
		private void OnShutdownSent(IAsyncResult ar) {
			lock(this) {	// synchronize
				int index = FindIndex(ar, m_SentList);
				if (index < 0) {
					return;
				} else {
					m_SentList.RemoveAt(index);
					try {
						if (!m_IsDisposed)
							m_Socket.Shutdown(SocketShutdown.Send);
					} catch {}
				}
				if (m_ShutdownCallback != null) {
					AsyncResult ret = m_ShutdownCallback;
					m_ShutdownCallback = null;
					ret.Notify(null);
				}

			}
		}
		public AsyncResult BeginShutdown(AsyncCallback callback, object state) {
			lock(this) {	// synchronize
				AsyncResult ret = new AsyncResult(callback, state, this);
				byte[] close = m_RecordLayer.GetControlBytes(ControlType.Shutdown);
				m_ShutdownCallback = ret;
				if (m_IsDisposed) {
					ret.Notify(null);
				} else {
					BeginSend(close, 0, close.Length, new AsyncResult(new AsyncCallback(this.OnShutdownSent), null, this), DataType.ProtocolData);
				}
				return ret;
			}
		}
		public AsyncResult EndShutdown(IAsyncResult ar) {
			AsyncResult ret;
			lock(this) {	// synchronize
				if (m_ShutdownCallback == null)
					return null;
				ret = m_ShutdownCallback;
				m_ShutdownCallback = null;
			}
            // do _not_ call this method inside the critical section, or the code may deadlock!
            while (!ret.IsCompleted) { 
                ret.AsyncWaitHandle.WaitOne(200, false);
            }
			return ret;
		}
		public void QueueRenegotiate() {
			lock(this) {	// synchronize
				byte[] negotiate = m_RecordLayer.GetControlBytes(ControlType.Renegotiate);
				if (negotiate != null) {
					BeginSend(negotiate, 0, negotiate.Length, null, DataType.ProtocolData);
				}
			}
		}
		public SecureSocket Parent {
			get {
				return m_Parent;
			}
		}
		public SslAlgorithms ActiveEncryption {
			get {
				if (m_RecordLayer != null)
					return m_RecordLayer.ActiveEncryption;
				else
					return SslAlgorithms.NONE;
			}
		}
		public Certificate RemoteCertificate {
			get {
				return m_RecordLayer.RemoteCertificate;
			}
		}
        public bool IsNegotiationCompleted {
            get {
                return m_RecordLayer != null && !m_RecordLayer.IsNegotiating();               
            }
        }

		private SecureSocket m_Parent;
		private Socket m_Socket;
		private RecordLayer m_RecordLayer;
		private CompatibilityLayer m_Compatibility;
		private ArrayList m_ToSendList;
		private ArrayList m_SentList;
		private TransferItem m_ActiveSend;
		private TransferItem m_ActiveReceive;
		private AsyncResult m_ShutdownCallback;
		private bool m_IsDisposed;
		private bool m_IsSending;
		private bool m_IsShuttingDown;
		private byte[] m_ReceiveBuffer;
		private XBuffer m_DecryptedBuffer;
		private const int m_ReceiveBufferLength = 4096;
/*
		public static void MsgMe(string where, string action, int size)
		{
			int id = System.Threading.Thread.CurrentThread.ManagedThreadId;
			Console.WriteLine("[{0}] {1}::{2} size={3}", id, where, action, size);
		}


		public static void MsgMe(string where, string action)
		{
			byte[] ddd = new byte[1];
 			MsgMe(where, action, false, ddd);
		}

		public static void MsgMe(string where, string action, TransferItem ti)
		{
			MsgMe(where, action, true, ti.Buffer);
		}

		public static void MsgMe(string where, string action, bool dumpit, byte[] todump)
		{
			int id = System.Threading.Thread.CurrentThread.ManagedThreadId;
			Console.WriteLine("[{0}] {1}::{2}", id, where, action);
			if (dumpit)
			{
				Console.WriteLine("{0}", HexDump(todump));
			}
		}

		public static void MsgMe(string where, string action, bool dumpit, byte[] todump, int size)
		{
			int id = System.Threading.Thread.CurrentThread.ManagedThreadId;
			Console.WriteLine("[{0}] {1}::{2} with buffer size {3}", id, where, action, size);
			if (dumpit)
			{
				Console.WriteLine("{0}", HexDump(todump, size));
			}
		}
		

		public static string HexDump(byte[] bytes, int length = -1, int bytesPerLine = 16)
		{
			if (bytes == null) return "<null>";
			int bytesLength = bytes.Length;
			if (length >= 0)
			{
				bytesLength = length;
			}

			char[] HexChars = "0123456789ABCDEF".ToCharArray();

			int firstHexColumn =
				  8                   // 8 characters for the address
				+ 3;                  // 3 spaces

			int firstCharColumn = firstHexColumn
				+ bytesPerLine * 3       // - 2 digit for the hexadecimal value and 1 space
				+ (bytesPerLine - 1) / 8 // - 1 extra space every 8 characters from the 9th
				+ 2;                  // 2 spaces 

			int lineLength = firstCharColumn
				+ bytesPerLine           // - characters to show the ascii value
				+ Environment.NewLine.Length; // Carriage return and line feed (should normally be 2)

			char[] line = (new String(' ', lineLength - 2) + Environment.NewLine).ToCharArray();
			int expectedLines = (bytesLength + bytesPerLine - 1) / bytesPerLine;
			StringBuilder result = new StringBuilder(expectedLines * lineLength);

			for (int i = 0; i < bytesLength; i += bytesPerLine)
			{
				line[0] = HexChars[(i >> 28) & 0xF];
				line[1] = HexChars[(i >> 24) & 0xF];
				line[2] = HexChars[(i >> 20) & 0xF];
				line[3] = HexChars[(i >> 16) & 0xF];
				line[4] = HexChars[(i >> 12) & 0xF];
				line[5] = HexChars[(i >> 8) & 0xF];
				line[6] = HexChars[(i >> 4) & 0xF];
				line[7] = HexChars[(i >> 0) & 0xF];

				int hexColumn = firstHexColumn;
				int charColumn = firstCharColumn;

				for (int j = 0; j < bytesPerLine; j++)
				{
					if (j > 0 && (j & 7) == 0) hexColumn++;
					if (i + j >= bytesLength)
					{
						line[hexColumn] = ' ';
						line[hexColumn + 1] = ' ';
						line[charColumn] = ' ';
					}
					else
					{
						byte b = bytes[i + j];
						line[hexColumn] = HexChars[(b >> 4) & 0xF];
						line[hexColumn + 1] = HexChars[b & 0xF];
						line[charColumn] = (b < 32 ? '�' : (char)b);
					}
					hexColumn += 3;
					charColumn++;
				}
				result.Append(line);
			}
			return result.ToString();
		}
*/

	}
}