﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using NetMQ.Actors;
using NetMQ.InProcActors;
using NetMQ.Sockets;
using NetMQ.zmq;

namespace NetMQ.WebSockets
{
    abstract class BaseShimHandler : IShimHandler<int>
    {
        private readonly NetMQContext m_context;
        private int m_id;

        private PairSocket m_messagesPipe;
        private StreamSocket m_stream;

        private Poller m_poller;

        private Dictionary<byte[], WebSocketClient> m_clients;

        public BaseShimHandler(NetMQContext context)
        {
            m_context = context;            

            m_clients = new Dictionary<byte[], WebSocketClient>(new ByteArrayEqualityComparer());
        }

        public void Initialise(int state)
        {
            m_id = state;
        }

        protected abstract void OnOutgoingMessage(NetMQMessage message);
        protected abstract void OnIncomingMessage(byte[] identity, NetMQMessage message);

        protected abstract void OnNewClient(byte[] identity);
        protected abstract void OnClientRemoved(byte[] identity);

        protected void WriteOutgoing(byte[] identity, byte[] message, bool more)
        {
            var outgoingData = Encode(message, more);

            m_stream.SendMore(identity).Send(outgoingData);
        }

        protected void WriteIngoing(NetMQMessage message)
        {
            m_messagesPipe.SendMessage(message);
        }

        public void RunPipeline(PairSocket shim)
        {
            shim.SignalOK();

            shim.ReceiveReady += OnShimReady;

            m_messagesPipe = m_context.CreatePairSocket();
            m_messagesPipe.Connect(string.Format("inproc://wsrouter-{0}", m_id));
            m_messagesPipe.ReceiveReady += OnMessagePipeReady;

            m_stream = m_context.CreateStreamSocket();
            m_stream.ReceiveReady += OnStreamReady;

            m_poller = new Poller(m_messagesPipe, shim, m_stream);

            m_messagesPipe.SignalOK();

            m_poller.Start();

            m_messagesPipe.Dispose();
            m_stream.Dispose();
        }

        private void OnShimReady(object sender, NetMQSocketEventArgs e)
        {
            string command = e.Socket.ReceiveString();

            if (command == WSSocket.BindCommand)
            {
                string address = e.Socket.ReceiveString();

                int errorCode = 0;

                try
                {
                    m_stream.Bind(address.Replace("ws://", "tcp://"));
                }
                catch (NetMQException ex)
                {
                    errorCode = (int)ex.ErrorCode;
                }

                byte[] bytes = BitConverter.GetBytes(errorCode);
                e.Socket.Send(bytes);
            }
            else if (command == ActorKnownMessages.END_PIPE)
            {
                m_poller.Stop(false);
            }
        }

        private void OnStreamReady(object sender, NetMQSocketEventArgs e)
        {
            byte[] identity = m_stream.Receive();

            WebSocketClient client;

            if (!m_clients.TryGetValue(identity, out client))
            {
                client = new WebSocketClient(m_stream, identity);
                client.IncomingMessage += OnIncomingMessage;
                m_clients.Add(identity, client);

                OnNewClient(identity);
            }

            client.OnDataReady();

            if (client.State == WebSocketClientState.Closed)
            {
                m_clients.Remove(identity);
                client.IncomingMessage -= OnIncomingMessage;
                OnClientRemoved(identity);
            }
        }

        private void OnIncomingMessage(object sender, NetMQMessageEventArgs e)
        {
            OnIncomingMessage(e.Identity, e.Message);
        }

        private void OnMessagePipeReady(object sender, NetMQSocketEventArgs e)
        {
            NetMQMessage request = m_messagesPipe.ReceiveMessage();
            
            OnOutgoingMessage(request);          
        }

        private static byte[] Encode(byte[] data, bool more)
        {
            int frameSize = 2 + 1 + data.Length;
            int payloadStartIndex = 2;
            int payloadLength = data.Length + 1;

            if (payloadLength > 125)
            {
                frameSize += 2;
                payloadStartIndex += 2;

                if (payloadLength > 0xFFFF) // 2 bytes max value
                {
                    frameSize += 6;
                    payloadStartIndex += 6;
                }
            }

            byte[] outgoingData = new byte[frameSize];

            outgoingData[0] = (byte)0x82; // Binary and Final      

            // No mask
            outgoingData[1] = 0x00;

            if (payloadLength <= 125)
            {
                outgoingData[1] |= (byte)(payloadLength & 127);
            }
            else if (payloadLength <= 0xFFFF) // maximum size of short
            {
                outgoingData[1] |= 126;
                outgoingData[2] = (byte)((payloadLength >> 8) & 0xFF);
                outgoingData[3] = (byte)(payloadLength & 0xFF);
            }
            else
            {
                outgoingData[1] |= 127;
                outgoingData[2] = 0;
                outgoingData[3] = 0;
                outgoingData[4] = 0;
                outgoingData[5] = 0;
                outgoingData[6] = (byte)((payloadLength >> 24) & 0xFF);
                outgoingData[7] = (byte)((payloadLength >> 16) & 0xFF);
                outgoingData[8] = (byte)((payloadLength >> 8) & 0xFF);
                outgoingData[9] = (byte)(payloadLength & 0xFF);
            }

            // more byte
            outgoingData[payloadStartIndex] = (byte)(more ? 1 : 0);
            payloadStartIndex++;

            // payload
            Buffer.BlockCopy(data, 0, outgoingData, payloadStartIndex, data.Length);
            return outgoingData;
        }
    }
}
