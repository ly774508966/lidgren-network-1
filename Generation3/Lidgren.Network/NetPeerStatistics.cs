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
using System.Text;

namespace Lidgren.Network
{
	public sealed class NetPeerStatistics
	{
		private NetPeer m_peer;

		internal int m_sentPackets;
		internal int m_receivedPackets;

		internal int m_sentBytes;
		internal int m_receivedBytes;

		internal long m_bytesAllocated;

		internal NetPeerStatistics(NetPeer peer)
		{
			m_peer = peer;
			Reset();
		}

		internal void Reset()
		{
			m_sentPackets = 0;
			m_receivedPackets = 0;
			m_sentBytes = 0;
			m_receivedBytes = 0;
			m_bytesAllocated = 0;
		}

		/// <summary>
		/// Gets the number of sent packets since the NetPeer was initialized
		/// </summary>
		public int SentPackets { get { return m_sentPackets; } }

		/// <summary>
		/// Gets the number of received packets since the NetPeer was initialized
		/// </summary>
		public int ReceivedPackets { get { return m_receivedPackets; } }

		/// <summary>
		/// Gets the number of sent bytes since the NetPeer was initialized
		/// </summary>
		public int SentBytes { get { return m_sentBytes; } }

		/// <summary>
		/// Gets the number of received bytes since the NetPeer was initialized
		/// </summary>
		public int ReceivedBytes { get { return m_receivedBytes; } }

		/// <summary>
		/// Gets the number of bytes allocated (and possibly garbage collected) for message storage
		/// </summary>
		public long BytesAllocated { get { return m_bytesAllocated; } }

		public override string ToString()
		{
			StringBuilder bdr = new StringBuilder();
			bdr.AppendLine(m_peer.ConnectionsCount.ToString() + " connections");
			bdr.AppendLine("Sent " + m_sentBytes + " bytes in " + m_sentPackets + " packets");
			bdr.AppendLine("Received " + m_receivedBytes + " bytes in " + m_receivedPackets + " packets");
			bdr.AppendLine("Allocated " + m_bytesAllocated + " bytes");
			return bdr.ToString();
		}
	}
}
