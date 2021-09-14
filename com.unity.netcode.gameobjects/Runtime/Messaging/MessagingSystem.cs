using System;
using System.Collections;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Unity.Collections;
using UnityEngine;

namespace Unity.Netcode
{

    public class InvalidMessageStructureException : SystemException
    {
        public InvalidMessageStructureException() { }
        public InvalidMessageStructureException(string issue) : base(issue) { }
    }

    public class MessagingSystem : IDisposable
    {
        #region Internal Types
        private struct ReceiveQueueItem
        {
            public FastBufferReader Reader;
            public MessageHeader Header;
            public ulong SenderId;
            public float Timestamp;
        }

        private struct SendQueueItem
        {
            public BatchHeader BatchHeader;
            public FastBufferWriter Writer;
            public readonly NetworkDelivery NetworkDelivery;

            public SendQueueItem(NetworkDelivery delivery, int writerSize, Allocator writerAllocator, int maxWriterSize = -1)
            {
                Writer = new FastBufferWriter(writerSize, writerAllocator, maxWriterSize);
                NetworkDelivery = delivery;
                BatchHeader = default;
            }
        }

        internal delegate void MessageHandler(ref FastBufferReader reader, NetworkContext context);
        #endregion

        #region Private Members
        private DynamicUnmanagedArray<ReceiveQueueItem> m_IncomingMessageQueue = new DynamicUnmanagedArray<ReceiveQueueItem>(16);

        private MessageHandler[] m_MessageHandlers = new MessageHandler[255];
        private Type[] m_ReverseTypeMap = new Type[255];

        private Dictionary<Type, byte> m_MessageTypes = new Dictionary<Type, byte>();
        private NativeHashMap<ulong, Ref<DynamicUnmanagedArray<SendQueueItem>>> m_SendQueues = new NativeHashMap<ulong, Ref<DynamicUnmanagedArray<SendQueueItem>>>(64, Allocator.Persistent);

        private List<INetworkHooks> m_Hooks = new List<INetworkHooks>();

        private byte m_HighMessageType;
        private object m_Owner;
        private IMessageSender m_MessageSender;
        private ulong m_LocalClientId;
        private bool m_Disposed;
        #endregion

        internal Type[] MessageTypes => m_ReverseTypeMap;
        internal MessageHandler[] MessageHandlers => m_MessageHandlers;
        internal int MessageHandlerCount => m_HighMessageType;

        internal byte GetMessageType(Type t)
        {
            return m_MessageTypes[t];
        }

        public MessagingSystem(IMessageSender messageSender, object owner, ulong localClientId = long.MaxValue)
        {
            try
            {
                m_LocalClientId = localClientId;
                m_MessageSender = messageSender;
                m_Owner = owner;

                var interfaceType = typeof(INetworkMessage);
                var implementationTypes = new List<Type>();
                foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
                {
                    foreach (var type in assembly.GetTypes())
                    {
                        if (type.IsInterface || type.IsAbstract)
                        {
                            continue;
                        }

                        if (interfaceType.IsAssignableFrom(type))
                        {
                            var attributes = type.GetCustomAttributes(typeof(Bind), false);
                            // If [Bind(ownerType)] isn't provided, it defaults to being bound to NetworkManager
                            // This is technically a breach of domain by having MessagingSystem know about the existence
                            // of NetworkManager... but ultimately, Bind is provided to support testing, not to support
                            // general use of MessagingSystem outside of Netcode for GameObjects, so having MessagingSystem
                            // know about NetworkManager isn't so bad. Especially since it's just a default value.
                            // This is just a convenience to keep us and our users from having to use
                            // [Bind(typeof(NetworkManager))] on every message - only tests that don't want to use
                            // the full NetworkManager need to worry about it.
                            var allowedToBind = attributes.Length == 0 && m_Owner is NetworkManager;
                            for (var i = 0; i < attributes.Length; ++i)
                            {
                                var bindAttribute = (Bind)attributes[i];
                                if (
                                    (bindAttribute.BoundType != null &&
                                     bindAttribute.BoundType.IsInstanceOfType(m_Owner)) ||
                                    (m_Owner == null && bindAttribute.BoundType == null))
                                {
                                    allowedToBind = true;
                                    break;
                                }
                            }

                            if (!allowedToBind)
                            {
                                continue;
                            }

                            implementationTypes.Add(type);
                        }
                    }
                }

                implementationTypes.Sort((a, b) => string.CompareOrdinal(a.FullName, b.FullName));
                foreach (var type in implementationTypes)
                {
                    RegisterMessageType(type);
                }
            }
            catch (Exception)
            {
                Dispose();
                throw;
            }
        }

        public void Dispose()
        {
            if (m_Disposed)
            {
                return;
            }

            var keys = m_SendQueues.GetKeyArray(Allocator.Temp);
            using (keys)
            {
                foreach (var key in keys)
                {
                    ClientDisconnected(key);
                }
            }
            m_SendQueues.Dispose();
            m_IncomingMessageQueue.Dispose();
            m_Disposed = true;
        }

        ~MessagingSystem()
        {
            Dispose();
        }

        public void SetLocalClientId(ulong localClientId)
        {
            m_LocalClientId = localClientId;
        }

        public void Hook(INetworkHooks hooks)
        {
            m_Hooks.Add(hooks);
        }

        private void RegisterMessageType(Type messageType)
        {
            if (!typeof(INetworkMessage).IsAssignableFrom(messageType))
            {
                throw new ArgumentException("RegisterMessageType types must be INetworkMessage types.");
            }

            var method = messageType.GetMethod("Receive");
            if (method == null)
            {
                throw new InvalidMessageStructureException(
                    $"{messageType.Name}: All INetworkMessage types must implement public static void Receive(ref FastBufferReader reader, NetworkContext context)");
            }

            var asDelegate = Delegate.CreateDelegate(typeof(MessageHandler), method, false);
            if (asDelegate == null)
            {
                throw new InvalidMessageStructureException(
                    $"{messageType.Name}: All INetworkMessage types must implement public static void Receive(ref FastBufferReader reader, NetworkContext context)");
            }

            m_MessageHandlers[m_HighMessageType] = (MessageHandler)asDelegate;
            m_ReverseTypeMap[m_HighMessageType] = messageType;
            m_MessageTypes[messageType] = m_HighMessageType++;
        }

        internal void HandleIncomingData(ulong clientId, ArraySegment<byte> data, float receiveTime)
        {
            unsafe
            {
                fixed (byte* nativeData = data.Array)
                {
                    var batchReader =
                        new FastBufferReader(nativeData, Allocator.None, data.Count, data.Offset);
                    if (!batchReader.TryBeginRead(sizeof(BatchHeader)))
                    {
                        NetworkLog.LogWarning("Received a packet too small to contain a BatchHeader. Ignoring it.");
                        return;
                    }

                    batchReader.ReadValue(out BatchHeader batchHeader);

                    for (var hookIdx = 0; hookIdx < m_Hooks.Count; ++hookIdx)
                    {
                        m_Hooks[hookIdx].OnBeforeReceiveBatch(clientId, batchHeader.BatchSize, batchReader.Length);
                    }

                    for (var messageIdx = 0; messageIdx < batchHeader.BatchSize; ++messageIdx)
                    {
                        if (!batchReader.TryBeginRead(sizeof(MessageHeader)))
                        {
                            NetworkLog.LogWarning("Received a batch that didn't have enough data for all of its batches, ending early!");
                            return;
                        }
                        batchReader.ReadValue(out MessageHeader messageHeader);
                        if (!batchReader.TryBeginRead(messageHeader.MessageSize))
                        {
                            NetworkLog.LogWarning("Received a message that claimed a size larger than the packet, ending early!");
                            return;
                        }
                        m_IncomingMessageQueue.Add(new ReceiveQueueItem
                        {
                            Header = messageHeader,
                            SenderId = clientId,
                            Timestamp = receiveTime,
                            // Copy the data for this message into a new FastBufferReader that owns that memory.
                            // We can't guarantee the memory in the ArraySegment stays valid because we don't own it,
                            // so we must move it to memory we do own.
                            Reader = new FastBufferReader(batchReader.GetUnsafePtrAtCurrentPosition(), Allocator.TempJob, messageHeader.MessageSize)
                        });
                        batchReader.Seek(batchReader.Position + messageHeader.MessageSize);
                    }
                    for (var hookIdx = 0; hookIdx < m_Hooks.Count; ++hookIdx)
                    {
                        m_Hooks[hookIdx].OnAfterReceiveBatch(clientId, batchHeader.BatchSize, batchReader.Length);
                    }
                }
            }
        }

        private bool CanReceive(ulong clientId, Type messageType)
        {
            for (var hookIdx = 0; hookIdx < m_Hooks.Count; ++hookIdx)
            {
                if (!m_Hooks[hookIdx].OnVerifyCanReceive(clientId, messageType))
                {
                    return false;
                }
            }

            return true;
        }

        public void HandleMessage(in MessageHeader header, ref FastBufferReader reader, ulong senderId, float timestamp)
        {
            var context = new NetworkContext
            {
                SystemOwner = m_Owner,
                SenderId = senderId,
                Timestamp = timestamp,
                Header = header
            };
            var type = m_ReverseTypeMap[header.MessageType];
            if (!CanReceive(senderId, type))
            {
                reader.Dispose();
                return;
            }

            for (var hookIdx = 0; hookIdx < m_Hooks.Count; ++hookIdx)
            {
                m_Hooks[hookIdx].OnBeforeReceiveMessage(senderId, type, reader.Length);
            }
            var handler = m_MessageHandlers[header.MessageType];
#pragma warning disable CS0728 // Warns that reader may be reassigned within the handler, but the handler does not reassign it.
            using (reader)
            {
                // No user-land message handler exceptions should escape the receive loop.
                // If an exception is throw, the message is ignored.
                // Example use case: A bad message is received that can't be deserialized and throws
                // an OverflowException because it specifies a length greater than the number of bytes in it
                // for some dynamic-length value.
                try
                {
                    handler.Invoke(ref reader, context);
                }
                catch (Exception e)
                {
                    Debug.LogError(e);
                }
            }
#pragma warning restore CS0728 // Warns that reader may be reassigned within the handler, but the handler does not reassign it.
            for (var hookIdx = 0; hookIdx < m_Hooks.Count; ++hookIdx)
            {
                m_Hooks[hookIdx].OnAfterReceiveMessage(senderId, type, reader.Length);
            }
        }

        internal void ProcessIncomingMessageQueue()
        {
            for (var i = 0; i < m_IncomingMessageQueue.Count; ++i)
            {
                // Avoid copies...
                ref var item = ref m_IncomingMessageQueue.GetValueRef(i);
                HandleMessage(item.Header, ref item.Reader, item.SenderId, item.Timestamp);
            }

            m_IncomingMessageQueue.Clear();
        }

        internal void ClientConnected(ulong clientId)
        {
            m_SendQueues[clientId] = DynamicUnmanagedArray<SendQueueItem>.CreateRef();
        }

        internal void ClientDisconnected(ulong clientId)
        {
            var queue = m_SendQueues[clientId];
            for (var i = 0; i < queue.Value.Count; ++i)
            {
                queue.Value.GetValueRef(i).Writer.Dispose();
            }

            DynamicUnmanagedArray<SendQueueItem>.ReleaseRef(queue);
            m_SendQueues.Remove(clientId);
        }

        private bool CanSend(ulong clientId, Type messageType, NetworkDelivery delivery)
        {
            for (var hookIdx = 0; hookIdx < m_Hooks.Count; ++hookIdx)
            {
                if (!m_Hooks[hookIdx].OnVerifyCanSend(clientId, messageType, delivery))
                {
                    return false;
                }
            }

            return true;
        }

        internal unsafe int SendMessage<TMessageType, TClientIdListType>(in TMessageType message, NetworkDelivery delivery, in TClientIdListType clientIds)
            where TMessageType : INetworkMessage
            where TClientIdListType : IReadOnlyList<ulong>
        {
            var maxSize = delivery == NetworkDelivery.ReliableFragmentedSequenced ? 64000 : 1300;
            var tmpSerializer = new FastBufferWriter(1300, Allocator.Temp, maxSize);
#pragma warning disable CS0728 // Warns that tmpSerializer may be reassigned within Serialize, but Serialize does not reassign it.
            using (tmpSerializer)
            {
                message.Serialize(ref tmpSerializer);

                for (var i = 0; i < clientIds.Count; ++i)
                {
                    var clientId = clientIds[i];

                    if (!CanSend(clientId, typeof(TMessageType), delivery))
                    {
                        continue;
                    }

                    for (var hookIdx = 0; hookIdx < m_Hooks.Count; ++hookIdx)
                    {
                        m_Hooks[hookIdx].OnBeforeSendMessage(clientId, typeof(TMessageType), delivery);
                    }

                    ref var sendQueueItem = ref m_SendQueues[clientId].Value;
                    if (sendQueueItem.Count == 0)
                    {
                        sendQueueItem.Add(new SendQueueItem(delivery, 1300, Allocator.TempJob,
                            maxSize));
                        sendQueueItem.GetValueRef(0).Writer.Seek(sizeof(BatchHeader));
                    }
                    else
                    {
                        ref var lastQueueItem = ref sendQueueItem.GetValueRef(sendQueueItem.Count - 1);
                        if (lastQueueItem.NetworkDelivery != delivery ||
                            lastQueueItem.Writer.MaxCapacity - lastQueueItem.Writer.Position < tmpSerializer.Length)
                        {
                            sendQueueItem.Add(new SendQueueItem(delivery, 1300, Allocator.TempJob,
                                maxSize));
                            sendQueueItem.GetValueRef(sendQueueItem.Count - 1).Writer.Seek(sizeof(BatchHeader));
                        }
                    }

                    ref var writeQueueItem = ref sendQueueItem.GetValueRef(sendQueueItem.Count - 1);
                    writeQueueItem.Writer.TryBeginWrite(sizeof(MessageHeader) + tmpSerializer.Length);
                    var header = new MessageHeader
                    {
                        MessageSize = (short)tmpSerializer.Length,
                        MessageType = m_MessageTypes[typeof(TMessageType)],
                    };


                    if (clientId == m_LocalClientId)
                    {
                        m_IncomingMessageQueue.Add(new ReceiveQueueItem
                        {
                            Header = header,
                            Reader = new FastBufferReader(ref tmpSerializer, Allocator.TempJob),
                            SenderId = clientId,
                            Timestamp = Time.realtimeSinceStartup
                        });
                        continue;
                    }

                    writeQueueItem.Writer.WriteValue(header);
                    writeQueueItem.Writer.WriteBytes(tmpSerializer.GetUnsafePtr(), tmpSerializer.Length);
                    writeQueueItem.BatchHeader.BatchSize++;
                    for (var hookIdx = 0; hookIdx < m_Hooks.Count; ++hookIdx)
                    {
                        m_Hooks[hookIdx].OnAfterSendMessage(clientId, typeof(TMessageType), delivery, tmpSerializer.Length + sizeof(MessageHeader));
                    }
                }
#pragma warning restore CS0728 // Warns that tmpSerializer may be reassigned within Serialize, but Serialize does not reassign it.

                return tmpSerializer.Length;
            }
        }

        private struct PointerListWrapper<T> : IReadOnlyList<T>
            where T : unmanaged
        {
            private unsafe T* m_Value;
            private int m_Length;

            internal unsafe PointerListWrapper(T* ptr, int length)
            {
                m_Value = ptr;
                m_Length = length;
            }

            public int Count
            {
                [MethodImpl(MethodImplOptions.AggressiveInlining)]
                get => m_Length;
            }

            public unsafe T this[int index]
            {
                [MethodImpl(MethodImplOptions.AggressiveInlining)]
                get => m_Value[index];
            }

            public IEnumerator<T> GetEnumerator()
            {
                throw new NotImplementedException();
            }

            IEnumerator IEnumerable.GetEnumerator()
            {
                return GetEnumerator();
            }
        }

        internal unsafe int SendMessage<T>(in T message, NetworkDelivery delivery,
            ulong* clientIds, int numClientIds)
            where T : INetworkMessage
        {
            return SendMessage(message, delivery, new PointerListWrapper<ulong>(clientIds, numClientIds));
        }

        internal unsafe int SendMessage<T>(in T message, NetworkDelivery delivery, ulong clientId)
            where T : INetworkMessage
        {
            ulong* clientIds = stackalloc ulong[] { clientId };
            return SendMessage(message, delivery, new PointerListWrapper<ulong>(clientIds, 1));
        }

        internal void ProcessSendQueues()
        {
            foreach (var kvp in m_SendQueues)
            {
                var clientId = kvp.Key;
                ref var sendQueueItem = ref kvp.Value.Value;
                for (var i = 0; i < sendQueueItem.Count; ++i)
                {
                    ref var queueItem = ref sendQueueItem.GetValueRef(i);
                    if (queueItem.BatchHeader.BatchSize == 0)
                    {
                        queueItem.Writer.Dispose();
                        continue;
                    }

                    for (var hookIdx = 0; hookIdx < m_Hooks.Count; ++hookIdx)
                    {
                        m_Hooks[hookIdx].OnBeforeSendBatch(clientId, queueItem.BatchHeader.BatchSize, queueItem.Writer.Length, queueItem.NetworkDelivery);
                    }

                    queueItem.Writer.Seek(0);
#if UNITY_EDITOR || DEVELOPMENT_BUILD
                    // Skipping the Verify and sneaking the write mark in because we know it's fine.
                    queueItem.Writer.AllowedWriteMark = 2;
#endif
                    queueItem.Writer.WriteValue(queueItem.BatchHeader);

                    m_MessageSender.Send(clientId, queueItem.NetworkDelivery, ref queueItem.Writer);
                    queueItem.Writer.Dispose();

                    for (var hookIdx = 0; hookIdx < m_Hooks.Count; ++hookIdx)
                    {
                        m_Hooks[hookIdx].OnAfterSendBatch(clientId, queueItem.BatchHeader.BatchSize, queueItem.Writer.Length, queueItem.NetworkDelivery);
                    }
                }
                sendQueueItem.Clear();
            }
        }
    }
}