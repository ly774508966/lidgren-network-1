﻿/* Copyright (c) 2010 Michael Lidgren

Permission is hereby granted, free of charge, to any person obtaining a copy of this software
and associated documentation files (the "Software"), to deal in the Software without
restriction, including without limitation the rights to use, copy, modify, merge, publish,
distribute, sublicense, and/or sell copies of the Software, and to permit persons to whom
the Software is furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all copies or
substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED,
INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR
PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE
LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT,
TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE
USE OR OTHER DEALINGS IN THE SOFTWARE.

*/
using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Threading;

namespace Lidgren.Network
{
	public partial class NetPeer
	{
		private EndPoint m_senderRemote;
		private int m_runSleepInMilliseconds = 1;
		internal byte[] m_receiveBuffer;
		internal byte[] m_sendBuffer;
		internal Socket m_socket;
		internal byte[] m_macAddressBytes;
		private int m_listenPort;

		private NetQueue<NetIncomingMessage> m_releasedIncomingMessages;
		private Queue<NetOutgoingMessage> m_unsentUnconnectedMessage;
		private Queue<IPEndPoint> m_unsentUnconnectedRecipients;

		// TODO: inline this method manually
		internal void ReleaseMessage(NetIncomingMessage msg)
		{
			m_releasedIncomingMessages.Enqueue(msg);
		}

		[System.Diagnostics.Conditional("DEBUG")]
		internal void VerifyNetworkThread()
		{
			Thread ct = System.Threading.Thread.CurrentThread;
			if (ct != m_networkThread)
				throw new NetException("Executing on wrong thread! Should be library system thread (is " + ct.Name + " mId " + ct.ManagedThreadId + ")");
		}

		//
		// Network loop
		//
		private void Run()
		{
			//
			// Initialize
			//
			VerifyNetworkThread();

			System.Net.NetworkInformation.PhysicalAddress pa = NetUtility.GetMacAddress();
			if (pa != null)
			{
				m_macAddressBytes = pa.GetAddressBytes();
				LogVerbose("Mac address is " + NetUtility.ToHexString(m_macAddressBytes));
			}
			else
			{
				LogWarning("Failed to get Mac address");
			}

			InitializeRecycling();

			LogDebug("Network thread started");

			lock (m_initializeLock)
			{
				if (m_status == NetPeerStatus.Running)
					return;

				m_statistics.Reset();

				// bind to socket
				IPEndPoint iep = null;
				try
				{
					iep = new IPEndPoint(m_configuration.LocalAddress, m_configuration.Port);
					EndPoint ep = (EndPoint)iep;

					m_socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
					m_socket.ReceiveBufferSize = m_configuration.ReceiveBufferSize;
					m_socket.SendBufferSize = m_configuration.SendBufferSize;
					m_socket.Blocking = false;
					m_socket.Bind(ep);

					IPEndPoint boundEp = m_socket.LocalEndPoint as IPEndPoint;
					LogDebug("Socket bound to " + boundEp + ": " + m_socket.IsBound);

					m_listenPort = boundEp.Port;

					m_uniqueIdentifier = NetUtility.GetMacAddress().GetHashCode() ^ boundEp.GetHashCode();

					m_receiveBuffer = new byte[m_configuration.ReceiveBufferSize];
					m_sendBuffer = new byte[m_configuration.SendBufferSize];

					LogVerbose("Initialization done");

					// only set Running if everything succeeds
					m_status = NetPeerStatus.Running;
				}
				catch (SocketException sex)
				{
					if (sex.SocketErrorCode == SocketError.AddressAlreadyInUse)
						throw new NetException("Failed to bind to port " + (iep == null ? "Null" : iep.ToString()) + " - Address already in use!", sex);
					throw;
				}
				catch (Exception ex)
				{
					throw new NetException("Failed to bind to " + (iep == null ? "Null" : iep.ToString()), ex);
				}
			}

			//
			// Network loop
			//
			m_runSleepInMilliseconds = m_configuration.m_runSleepMillis;

			do
			{
				try
				{
					Heartbeat();
				}
				catch (Exception ex)
				{
					LogWarning(ex.ToString());
				}

				// wait here to give cpu to other threads/processes; also to collect messages for aggregate packet
				Thread.Sleep(m_runSleepInMilliseconds);
			} while (m_status == NetPeerStatus.Running);

			//
			// perform shutdown
			//
			LogDebug("Shutting down...");

			// disconnect and make one final heartbeat
			foreach (NetConnection conn in m_connections)
				if (conn.m_status == NetConnectionStatus.Connected || conn.m_status == NetConnectionStatus.Connecting)
					conn.Disconnect(m_shutdownReason);

			// one final heartbeat, will send stuff and do disconnect
			Heartbeat();

			lock (m_initializeLock)
			{
				try
				{
					if (m_socket != null)
					{
						m_socket.Shutdown(SocketShutdown.Receive);
						m_socket.Close(2); // 2 seconds timeout
					}
				}
				finally
				{
					m_socket = null;
					m_status = NetPeerStatus.NotRunning;
					LogDebug("Shutdown complete");
				}
			}

			//
			// TODO: make sure everything is DE-initialized (release etc)
			//

			return;
		}

		private void Heartbeat()
		{
			VerifyNetworkThread();

#if DEBUG
			// send delayed packets
			SendDelayedPackets();
#endif

			// connection approval
			CheckPendingConnections();

			double now = NetTime.Now;
			
			// do connection heartbeats
			foreach (NetConnection conn in m_connections)
			{
				conn.Heartbeat(now);
				if (conn.m_status == NetConnectionStatus.Disconnected)
				{
					RemoveConnection(conn);
					break; // can't continue iteration here
				}
			}

			// send unconnected sends
			if (m_unsentUnconnectedMessage.Count > 0)
			{
				lock (m_unsentUnconnectedMessage)
				{
					while (m_unsentUnconnectedMessage.Count > 0)
					{
						NetOutgoingMessage msg = m_unsentUnconnectedMessage.Dequeue();
						IPEndPoint recipient = m_unsentUnconnectedRecipients.Dequeue();

						int ptr = msg.Encode(m_sendBuffer, 0, null);

						if (recipient.Address.Equals(IPAddress.Broadcast))
						{
							// send using broadcast
							try
							{
								m_socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.Broadcast, true);
								SendPacket(ptr, recipient);
							}
							finally
							{
								m_socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.Broadcast, false);
							}
						}
						else
						{
							// send normally
							SendPacket(ptr, recipient);
						}
					}
				}
			}

			//
			// read from socket
			//
			do
			{
				if (m_socket == null)
					return;

				if (!m_socket.Poll(3000, SelectMode.SelectRead)) // wait up to 3 ms for data to arrive
					return;

				//if (m_socket == null || m_socket.Available < 1)
				//	return;

				int bytesReceived = 0;
				try
				{
					bytesReceived = m_socket.ReceiveFrom(m_receiveBuffer, 0, m_receiveBuffer.Length, SocketFlags.None, ref m_senderRemote);
				}
				catch (SocketException sx)
				{
					// no good response to this yet
					if (sx.ErrorCode == 10054)
					{
						// connection reset by peer, aka forcibly closed
						// we should shut down the connection; but m_senderRemote seemingly cannot be trusted, so which connection should we shut down?!
						LogWarning("Connection reset by peer, seemingly from " + m_senderRemote);
						return;
					}

					LogWarning(sx.ToString());
					return;
				}

				if (bytesReceived < 1)
					return;

				m_statistics.m_receivedPackets++;
				m_statistics.m_receivedBytes += bytesReceived;

				// renew time
				now = NetTime.Now;

				//LogVerbose("Received " + bytesReceived + " bytes");

				IPEndPoint ipsender = (IPEndPoint)m_senderRemote;

				NetConnection sender = null;
				m_connectionLookup.TryGetValue(ipsender, out sender);

				if (sender != null)
					sender.m_lastHeardFrom = now;

				int ptr = 0;
				NetMessageType mtp;
				NetMessageLibraryType ltp = NetMessageLibraryType.Error;

				//
				// parse packet into messages
				//
				while ((bytesReceived - ptr) >= NetPeer.kMinPacketHeaderSize)
				{
					// get NetMessageType
					mtp = (NetMessageType)m_receiveBuffer[ptr++];

					// get NetmessageLibraryType?
					if (mtp == NetMessageType.Library)
						ltp = (NetMessageLibraryType)m_receiveBuffer[ptr++];

					// get sequence number?
					ushort sequenceNumber;
					if (mtp >= NetMessageType.UserSequenced)
						sequenceNumber = (ushort)(m_receiveBuffer[ptr++] | (m_receiveBuffer[ptr++] << 8));
					else
						sequenceNumber = 0;

					// get payload length
					int payloadLength = (int)m_receiveBuffer[ptr++];
					if ((payloadLength & 128) == 128) // large payload
						payloadLength = (payloadLength & 127) | (m_receiveBuffer[ptr++] << 7);

					if ((ptr + payloadLength) > bytesReceived)
					{
						LogWarning("Malformed message from " + ipsender.ToString() + "; not enough bytes");
						break;
					}

					//
					// handle incoming message
					//

					if (mtp == NetMessageType.Error)
					{
						LogError("Malformed message; no message type!");
						continue;
					}

					if (mtp == NetMessageType.Library)
					{
						if (sender == null)
							HandleUnconnectedLibraryMessage(ltp, ptr, payloadLength, ipsender);
						else
							sender.HandleLibraryMessage(now, ltp, ptr, payloadLength);
					}
					else
					{
						if (sender == null)
							HandleUnconnectedUserMessage(ptr, payloadLength, ipsender);
						else
							sender.HandleUserMessage(now, mtp, sequenceNumber, ptr, payloadLength);
					}

					ptr += payloadLength;
				}

				if (ptr < bytesReceived)
				{
					// malformed packet
					LogWarning("Malformed packet from " + sender + " (" + ipsender + "); " + (ptr - bytesReceived) + " stray bytes");
					continue;
				}
			} while (true);
			// heartbeat done
		}

		private void HandleUnconnectedLibraryMessage(NetMessageLibraryType libType, int ptr, int payloadLength, IPEndPoint senderEndPoint)
		{
			VerifyNetworkThread();

			if (libType != NetMessageLibraryType.Connect)
			{
				LogWarning("Received unconnected library message of type " + libType);
				return;
			}

			//
			// Handle NetMessageLibraryType.Connect
			//

			if (!m_configuration.m_acceptIncomingConnections)
			{
				LogWarning("Connect received; but we're not accepting incoming connections!");
				return;
			}

			string appIdent;
			byte[] remoteHail = null;
			try
			{
				NetIncomingMessage reader = new NetIncomingMessage();

				reader.m_data = GetStorage(payloadLength);
				Buffer.BlockCopy(m_receiveBuffer, ptr, reader.m_data, 0, payloadLength);
				ptr += payloadLength;
				reader.m_bitLength = payloadLength * 8;
				appIdent = reader.ReadString();
				int hailLen = (int)reader.ReadVariableUInt32();
				if (hailLen > 0 && hailLen < m_configuration.MaximumTransmissionUnit)
				{
					remoteHail = new byte[hailLen];
					reader.ReadBytes(remoteHail, 0, hailLen);
				}
			}
			catch (Exception ex)
			{
				// malformed connect packet
				LogWarning("Malformed connect packet from " + senderEndPoint + " - " + ex.ToString());
				return;
			}

			if (appIdent.Equals(m_configuration.AppIdentifier) == false)
			{
				// wrong app ident
				LogWarning("Connect received with wrong appidentifier (need '" + m_configuration.AppIdentifier + "' found '" + appIdent + "') from " + senderEndPoint);
				return;
			}

			// ok, someone wants to connect to us, and we're accepting connections!
			if (m_connections.Count >= m_configuration.MaximumConnections)
			{
				HandleServerFull(senderEndPoint);
				return;
			}

			NetConnection conn = new NetConnection(this, senderEndPoint);
			conn.m_connectionInitiator = false;
			conn.m_localHailData = null; // TODO: use some default hail data set in netpeer?
			conn.m_remoteHailData = remoteHail;

			if (m_configuration.IsMessageTypeEnabled(NetIncomingMessageType.ConnectionApproval))
			{
				// do connection approval before accepting this connection
				AddPendingConnection(conn);
				return;
			}

			AcceptConnection(conn);
			return;
		}

		private void HandleUnconnectedUserMessage(int ptr, int payloadLength, IPEndPoint senderEndPoint)
		{
			VerifyNetworkThread();

			if (m_configuration.IsMessageTypeEnabled(NetIncomingMessageType.UnconnectedData))
			{
				NetIncomingMessage ium = CreateIncomingMessage(NetIncomingMessageType.UnconnectedData, m_receiveBuffer, ptr, payloadLength);
				ium.m_senderEndPoint = senderEndPoint;
				ReleaseMessage(ium);
			}
		}

		private void AcceptConnection(NetConnection conn)
		{
			lock (m_connections)
			{
				m_connections.Add(conn);
				m_connectionLookup[conn.m_remoteEndPoint] = conn;
			}
			conn.SetStatus(NetConnectionStatus.Connecting, "Connecting");

			// send connection response
			LogVerbose("Sending LibraryConnectResponse");
			NetOutgoingMessage reply = CreateMessage(2 + (conn.m_localHailData == null ? 0 : conn.m_localHailData.Length));
			reply.m_type = NetMessageType.Library;
			reply.m_libType = NetMessageLibraryType.ConnectResponse;
			if (conn.m_localHailData != null)
				reply.Write(conn.m_localHailData);

			SendImmediately(conn, reply);
			conn.m_connectInitationTime = NetTime.Now;

			return;
		}

		internal void RemoveConnection(NetConnection conn)
		{
			conn.Dispose();
			m_connections.Remove(conn);
			m_connectionLookup.Remove(conn.m_remoteEndPoint);
		}

		private void HandleServerFull(IPEndPoint connecter)
		{
			const string rejectMessage = "Server is full!";
			NetOutgoingMessage reply = CreateLibraryMessage(NetMessageLibraryType.Disconnect, rejectMessage);
			EnqueueUnconnectedMessage(reply, connecter);
		}

		private void EnqueueUnconnectedMessage(NetOutgoingMessage msg, IPEndPoint recipient)
		{
			Interlocked.Increment(ref msg.m_inQueueCount);
			lock (m_unsentUnconnectedMessage)
			{
				m_unsentUnconnectedMessage.Enqueue(msg);
				m_unsentUnconnectedRecipients.Enqueue(recipient);
			}
		}

		internal static NetDeliveryMethod GetDeliveryMethod(NetMessageType mtp)
		{
			if (mtp >= NetMessageType.UserReliableOrdered)
				return NetDeliveryMethod.ReliableOrdered;
			else if (mtp >= NetMessageType.UserReliableSequenced)
				return NetDeliveryMethod.ReliableSequenced;
			else if (mtp >= NetMessageType.UserReliableUnordered)
				return NetDeliveryMethod.ReliableUnordered;
			else if (mtp >= NetMessageType.UserSequenced)
				return NetDeliveryMethod.UnreliableSequenced;
			return NetDeliveryMethod.Unreliable;
		}

		internal void SendImmediately(NetConnection conn, NetOutgoingMessage msg)
		{
#if DEBUG
			if (msg.m_type != NetMessageType.Library)
				throw new NetException("SendImmediately can only send library (non-reliable) messages");
#endif
			int len = msg.Encode(m_sendBuffer, 0, conn);
			Interlocked.Decrement(ref msg.m_inQueueCount);
			SendPacket(len, conn.m_remoteEndPoint);
		}
	}
}