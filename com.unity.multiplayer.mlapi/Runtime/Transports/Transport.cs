using System;
using System.Collections.Generic;
using MLAPI.Transports.Tasks;
using UnityEngine;

namespace MLAPI.Transports
{
    /// <summary>
    /// A network transport
    /// </summary>
    [RequireComponent(typeof(NetworkingManager))]
    public abstract class Transport : MonoBehaviour
    {
        /// <summary>
        /// Delegate used to request channels on the underlying transport.
        /// </summary>
        public delegate void RequestChannelsDelegate(List<TransportChannel> channels);

        /// <summary>
        /// Delegate called when the transport wants to know what channels to register.
        /// </summary>
        public event RequestChannelsDelegate OnChannelRegistration;

        [SerializeField]
        protected NetworkingManager NetManager;

        /// <summary>
        /// A constant clientId that represents the server.
        /// When this value is found in methods such as Send, it should be treated as a placeholder that means "the server"
        /// </summary>
        public abstract ulong ServerClientId { get; }

        /// <summary>
        /// Gets a value indicating whether this <see cref="T:MLAPI.Transports.Transport"/> is supported in the current runtime context.
        /// This is used by multiplex adapters.
        /// </summary>
        /// <value><c>true</c> if is supported; otherwise, <c>false</c>.</value>
        public virtual bool IsSupported => true;

        private TransportChannel[] _channelsCache = null;

        internal void ResetChannelCache()
        {
            _channelsCache = null;
        }

        public TransportChannel[] MLAPI_CHANNELS
        {
            get
            {
                if (_channelsCache == null)
                {
                    List<TransportChannel> channels = new List<TransportChannel>();

                    if (OnChannelRegistration != null)
                    {
                        OnChannelRegistration(channels);
                    }

                    _channelsCache = new TransportChannel[MLAPI_INTERNAL_CHANNELS.Length + channels.Count];

                    for (int i = 0; i < MLAPI_INTERNAL_CHANNELS.Length; i++)
                    {
                        _channelsCache[i] = MLAPI_INTERNAL_CHANNELS[i];
                    }

                    for (int i = 0; i < channels.Count; i++)
                    {
                        _channelsCache[i + MLAPI_INTERNAL_CHANNELS.Length] = channels[i];
                    }
                }

                return _channelsCache;
            }
        }

        public const byte MLAPI_INTERNAL_CHANNEL = 0;
        public const byte MLAPI_STDRPC_CHANNEL = 1;
        public const byte MLAPI_TIME_SYNC_CHANNEL = 2;

        public static string GetChannelString(byte channel)
        {
            string channelName = "";
            if (!TransportChannel.ChannelByteToString.TryGetValue(channel, out channelName))
            {
                channelName = "MLAPI_INTERNAL";
            }

            return channelName;
        }

        public static byte GetChannelByte(string channelName)
        {
            byte channel = MLAPI_INTERNAL_CHANNEL;
            TransportChannel.ChannelStringToByte.TryGetValue(channelName, out channel);
            return channel;
        }


        /// <summary>
        /// The channels the MLAPI will use when sending internal messages.
        /// </summary>
        private TransportChannel[] MLAPI_INTERNAL_CHANNELS = new TransportChannel[]
        {
            new TransportChannel("MLAPI_INTERNAL", ChannelType.ReliableFragmentedSequenced, MLAPI_INTERNAL_CHANNEL),
            new TransportChannel("STDRPC", ChannelType.ReliableSequenced, MLAPI_STDRPC_CHANNEL),
            new TransportChannel("MLAPI_TIME_SYNC", ChannelType.Unreliable, MLAPI_TIME_SYNC_CHANNEL),
            new TransportChannel("MLAPI_DEFAULT_MESSAGE", ChannelType.Reliable, 3),
            new TransportChannel("MLAPI_POSITION_UPDATE", ChannelType.UnreliableSequenced, 4),
            new TransportChannel("MLAPI_ANIMATION_UPDATE", ChannelType.ReliableSequenced,5 ),
            new TransportChannel("MLAPI_NAV_AGENT_STATE", ChannelType.ReliableSequenced, 6),
            new TransportChannel("MLAPI_NAV_AGENT_CORRECTION", ChannelType.UnreliableSequenced, 7),
        };

        /// <summary>
        /// Delegate for transport events.
        /// </summary>
        public delegate void TransportEventDelegate(NetEventType type, ulong clientId, byte channel, ArraySegment<byte> payload, float receiveTime);

        /// <summary>
        /// Occurs when the transport has a new transport event. Can be used to make an event based transport instead of a poll based.
        /// Invokation has to occur on the Unity thread in the Update loop.
        /// </summary>
        public event TransportEventDelegate OnTransportEvent;

        /// <summary>
        /// Invokes the <see cref="OnTransportEvent"/>. Invokation has to occur on the Unity thread in the Update loop.
        /// </summary>
        /// <param name="type">The event type</param>
        /// <param name="clientId">The clientId this event is for</param>
        /// <param name="channelName">The channel the data arrived at. This is usually used when responding to things like RPCs</param>
        /// <param name="payload">The incoming data payload</param>
        /// <param name="receiveTime">The time the event was received, as reported by Time.realtimeSinceStartup.</param>
        protected void InvokeOnTransportEvent(NetEventType type, ulong clientId, byte channel, ArraySegment<byte> payload, float receiveTime)
        {
            OnTransportEvent?.Invoke(type, clientId, channel, payload, receiveTime);
        }

        /// <summary>
        /// Send a payload to the specified clientId, data and channelName.
        /// </summary>
        /// <param name="clientId">The clientId to send to</param>
        /// <param name="data">The data to send</param>
        /// <param name="channelName">The channel to send data to</param>
        public abstract void Send(ulong clientId, ArraySegment<byte> data, byte channel);

        /// <summary>
        /// Polls for incoming events, with an extra output parameter to report the precise time the event was received.
        /// </summary>
        /// <param name="clientId">The clientId this event is for</param>
        /// <param name="channelName">The channel the data arrived at. This is usually used when responding to things like RPCs</param>
        /// <param name="payload">The incoming data payload</param>
        /// <param name="receiveTime">The time the event was received, as reported by Time.realtimeSinceStartup.</param>
        /// <returns>Returns the event type</returns>
        public abstract NetEventType PollEvent(out ulong clientId, out byte channel, out ArraySegment<byte> payload, out float receiveTime);

        /// <summary>
        /// Connects client to server
        /// </summary>
        public abstract SocketTasks StartClient();

        /// <summary>
        /// Starts to listen for incoming clients.
        /// </summary>
        public abstract SocketTasks StartServer();

        /// <summary>
        /// Disconnects a client from the server
        /// </summary>
        /// <param name="clientId">The clientId to disconnect</param>
        public abstract void DisconnectRemoteClient(ulong clientId);

        /// <summary>
        /// Disconnects the local client from the server
        /// </summary>
        public abstract void DisconnectLocalClient();

        /// <summary>
        /// Gets the round trip time for a specific client. This method is optional
        /// </summary>
        /// <param name="clientId">The clientId to get the rtt from</param>
        /// <returns>Returns the round trip time in milliseconds </returns>
        public abstract ulong GetCurrentRtt(ulong clientId);

        /// <summary>
        /// Shuts down the transport
        /// </summary>
        public abstract void Shutdown();

        /// <summary>
        /// Initializes the transport
        /// </summary>
        public abstract void Init();
    }
}