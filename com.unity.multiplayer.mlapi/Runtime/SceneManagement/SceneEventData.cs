using System.Collections.Generic;
using System;
using System.IO;
using System.Linq;
using UnityEngine;
using UnityEngine.SceneManagement;


namespace Unity.Netcode
{
    /// <summary>
    /// Used by <see cref="NetworkSceneManager"/> for <see cref="MessageQueueContainer.MessageType.SceneEvent"/> messages
    /// Note: This is only when <see cref="NetworkConfig.EnableSceneManagement"/> is enabled
    /// </summary>
    public class SceneEventData : IDisposable
    {
        /// <summary>
        /// The different types of scene events communicated between a server and client.
        /// Scene event types can be:
        /// A Server To Client Event (S2C)
        /// A Client to Server Event (C2S)
        /// </summary>
        public enum SceneEventTypes
        {
            /// <summary>
            /// Load a scene
            /// Invocation: Server Side
            /// Message Flow: Server to client
            /// Event Notification: Both server and client are notified a load scene event started
            /// </summary>
            S2C_Load,
            /// <summary>
            /// Unload a scene
            /// Invocation: Server Side
            /// Message Flow: Server to client
            /// Event Notification: Both server and client are notified an unload scene event started
            /// </summary>
            S2C_Unload,
            /// <summary>
            /// Synchronize current game session state for approved clients
            /// Invocation: Server Side
            /// Message Flow: Server to client
            /// Event Notification: Server and Client receives a local notification (server receives the ClientId being synchronized)
            /// </summary>
            S2C_Sync,
            /// <summary>
            /// Game session re-synchronization of NetworkOjects that were destroyed during a <see cref="S2C_Sync"/> event
            /// Invocation: Server Side
            /// Message Flow: Server to client
            /// Event Notification: Both server and client receive a local notification
            /// </summary>
            S2C_ReSync,
            /// <summary>
            /// All clients have finished loading a scene
            /// Invocation: Server Side
            /// Message Flow: Server to Client
            /// Event Notification: Both server and client receive a local notification containing the clients that finished
            /// as well as the clients that timed out (if any).
            /// </summary>
            S2C_LoadComplete,
            /// <summary>
            /// All clients have unloaded a scene
            /// Invocation: Server Side
            /// Message Flow: Server to Client
            /// Event Notification: Both server and client receive a local notification containing the clients that finished
            /// as well as the clients that timed out (if any).
            /// </summary>
            S2C_UnLoadComplete,
            /// <summary>
            /// A client has finished loading a scene
            /// Invocation: Client Side
            /// Message Flow: Client to Server
            /// Event Notification: Both server and client receive a local notification
            /// </summary>
            C2S_LoadComplete,
            /// <summary>
            /// A client has finished unloading a scene
            /// Invocation: Client Side
            /// Message Flow: Client to Server
            /// Event Notification: Both server and client receive a local notification
            /// </summary>
            C2S_UnloadComplete,
            /// <summary>
            /// A client has finished synchronizing from a <see cref="S2C_Sync"/> event
            /// Invocation: Client Side
            /// Message Flow: Client to Server
            /// Event Notification: Both server and client receive a local notification
            /// </summary>
            C2S_SyncComplete,
        }

        internal SceneEventTypes SceneEventType;
        internal LoadSceneMode LoadSceneMode;
        internal Guid SceneEventGuid;

        internal uint SceneIndex;
        internal ulong TargetClientId;

        private Dictionary<uint, List<NetworkObject>> m_SceneNetworkObjects;
        private Dictionary<uint, long> m_SceneNetworkObjectDataOffsets;

        /// <summary>
        /// Client or Server Side:
        /// Client side: Generates a list of all NetworkObjects by their NetworkObjectId that was spawned during th synchronization process
        /// Server side: Compares list from client to make sure client didn't drop a message about a NetworkObject being despawned while it
        /// was synchronizing (if so server will send another message back to the client informing the client of NetworkObjects to remove)
        /// spawned during an initial synchronization.
        /// </summary>
        private List<NetworkObject> m_NetworkObjectsSync = new List<NetworkObject>();

        /// <summary>
        /// Server Side Re-Synchronization:
        /// If there happens to be NetworkObjects in the final Event_Sync_Complete message that are no longer spawned,
        /// the server will compile a list and send back an Event_ReSync message to the client.
        /// </summary>
        private List<ulong> m_NetworkObjectsToBeRemoved = new List<ulong>();

        internal PooledNetworkBuffer InternalBuffer;

        private NetworkManager m_NetworkManager;

        /// <summary>
        /// Client side and only applies to the following scene event types:
        /// <see cref="C2S_LoadComplete"/>
        /// <see cref="C2S_UnLoadComplete"/>
        /// </summary>
        internal SceneEvent SceneEvent;

        internal List<ulong> ClientsCompleted;
        internal List<ulong> ClientsTimedOut;

        /// <summary>
        /// Client Side:
        /// Gets the next scene index to be loaded for approval and/or late joining
        /// </summary>
        /// <returns></returns>
        internal uint GetNextSceneSynchronizationIndex()
        {
            if (m_SceneNetworkObjectDataOffsets.ContainsKey(SceneIndex))
            {
                return SceneIndex;
            }
            return m_SceneNetworkObjectDataOffsets.First().Key;
        }

        /// <summary>
        /// Client Side:
        /// Determines if all scenes have been processed during the synchronization process
        /// </summary>
        /// <returns>true/false</returns>
        internal bool IsDoneWithSynchronization()
        {
            return (m_SceneNetworkObjectDataOffsets.Count == 0);
        }

        /// <summary>
        /// Server Side:
        /// Called just before the synchronization process
        /// </summary>
        internal void InitializeForSynch()
        {
            if (m_SceneNetworkObjects == null)
            {
                m_SceneNetworkObjects = new Dictionary<uint, List<NetworkObject>>();
            }
            else
            {
                m_SceneNetworkObjects.Clear();
            }
        }

        /// <summary>
        /// Server Side:
        /// Used during the synchronization process to associate NetworkObjects with scenes
        /// </summary>
        /// <param name="sceneIndex"></param>
        /// <param name="networkObject"></param>
        internal void AddNetworkObjectForSynch(uint sceneIndex, NetworkObject networkObject)
        {
            if (!m_SceneNetworkObjects.ContainsKey(sceneIndex))
            {
                m_SceneNetworkObjects.Add(sceneIndex, new List<NetworkObject>());
            }

            m_SceneNetworkObjects[sceneIndex].Add(networkObject);
        }

        /// <summary>
        /// Client and Server:
        /// Determines if the scene event type was intended for the client ( or server )
        /// </summary>
        /// <returns>true (client should handle this message) false (server should handle this message)</returns>
        internal bool IsSceneEventClientSide()
        {
            switch (SceneEventType)
            {
                case SceneEventTypes.S2C_Load:
                case SceneEventTypes.S2C_Unload:
                case SceneEventTypes.S2C_Sync:
                case SceneEventTypes.S2C_ReSync:
                case SceneEventTypes.S2C_LoadComplete:
                case SceneEventTypes.S2C_UnLoadComplete:
                    {
                        return true;
                    }
            }
            return false;
        }

        /// <summary>
        /// Server Side:
        /// Sorts the NetworkObjects to assure proper order of operations for custom Network Prefab handlers
        /// that implement the INetworkPrefabInstanceHandler interface.
        /// </summary>
        /// <param name="first"></param>
        /// <param name="second"></param>
        /// <returns></returns>
        private int SortNetworkObjects(NetworkObject first, NetworkObject second)
        {
            var doesFirstHaveHandler = m_NetworkManager.PrefabHandler.ContainsHandler(first);
            var doesSecondHaveHandler = m_NetworkManager.PrefabHandler.ContainsHandler(second);
            if (doesFirstHaveHandler != doesSecondHaveHandler)
            {
                if (doesFirstHaveHandler)
                {
                    return 1;
                }
                else
                {
                    return -1;
                }
            }
            return 0;
        }

        /// <summary>
        /// Client and Server Side:
        /// Serializes data based on the SceneEvent type (<see cref="SceneEventTypes"/>)
        /// </summary>
        /// <param name="writer"><see cref="NetworkWriter"/> to write the scene event data</param>
        internal void OnWrite(NetworkWriter writer)
        {
            writer.WriteByte((byte)SceneEventType);

            writer.WriteByte((byte)LoadSceneMode);

            if (SceneEventType != SceneEventTypes.S2C_Sync)
            {
                writer.WriteByteArray(SceneEventGuid.ToByteArray());
            }

            writer.WriteUInt32Packed(SceneIndex);

            if (SceneEventType == SceneEventTypes.S2C_Sync)
            {
                writer.WriteInt32Packed(m_SceneNetworkObjects.Count());

                if (m_SceneNetworkObjects.Count() > 0)
                {
                    foreach (var keypair in m_SceneNetworkObjects)
                    {
                        writer.WriteUInt32Packed(keypair.Key);
                        writer.WriteInt32Packed(keypair.Value.Count);
                        var positionStart = writer.GetStream().Position;
                        // Size Place Holder (For offset purposes, needs to not be packed)
                        writer.WriteUInt32(0);
                        var totalBytes = 0;

                        // Sort NetworkObjects so any NetworkObjects with a PrefabHandler are sorted to be after all other NetworkObjects
                        // This will assure any INetworkPrefabInstanceHandler instance is registered before we try to spawn the NetworkObjects
                        // on the client side.
                        keypair.Value.Sort(SortNetworkObjects);

                        foreach (var networkObject in keypair.Value)
                        {
                            var noStart = writer.GetStream().Position;

                            networkObject.SerializeSceneObject(writer, TargetClientId);
                            var noStop = writer.GetStream().Position;
                            totalBytes += (int)(noStop - noStart);
                        }
                        var positionEnd = writer.GetStream().Position;
                        var bytesWritten = (uint)(positionEnd - (positionStart + sizeof(uint)));
                        writer.GetStream().Position = positionStart;
                        // Write the total size written to the stream by NetworkObjects being serialized
                        writer.WriteUInt32(bytesWritten);
                        writer.GetStream().Position = positionEnd;
                    }
                }
            }

            if (SceneEventType == SceneEventTypes.C2S_SyncComplete)
            {
                WriteClientSynchronizationResults(writer);
            }

            if (SceneEventType == SceneEventTypes.S2C_ReSync)
            {
                WriteClientReSynchronizationData(writer);
            }

            if (SceneEventType == SceneEventTypes.S2C_LoadComplete || SceneEventType == SceneEventTypes.S2C_UnLoadComplete)
            {
                WriteSceneEventProgressDone(writer);
            }
        }

        /// <summary>
        /// Client and Server Side:
        /// Deserialize data based on the SceneEvent type.
        /// </summary>
        /// <param name="reader"></param>
        internal void OnRead(NetworkReader reader)
        {
            var sceneEventTypeValue = reader.ReadByte();

            if (Enum.IsDefined(typeof(SceneEventTypes), sceneEventTypeValue))
            {
                SceneEventType = (SceneEventTypes)sceneEventTypeValue;
            }
            else
            {
                Debug.LogError($"Serialization Read Error: {nameof(SceneEventType)} vale {sceneEventTypeValue} is not within the range of the defined {nameof(SceneEventTypes)} enumerator!");
            }

            var loadSceneModeValue = reader.ReadByte();

            if (Enum.IsDefined(typeof(LoadSceneMode), loadSceneModeValue))
            {
                LoadSceneMode = (LoadSceneMode)loadSceneModeValue;
            }
            else
            {
                Debug.LogError($"Serialization Read Error: {nameof(LoadSceneMode)} vale {loadSceneModeValue} is not within the range of the defined {nameof(LoadSceneMode)} enumerator!");
            }

            if (SceneEventType != SceneEventTypes.S2C_Sync)
            {
                SceneEventGuid = new Guid(reader.ReadByteArray());
            }

            SceneIndex = reader.ReadUInt32Packed();

            if (SceneEventType == SceneEventTypes.S2C_Sync)
            {
                m_NetworkObjectsSync.Clear();
                var keyPairCount = reader.ReadInt32Packed();

                if (m_SceneNetworkObjectDataOffsets == null)
                {
                    m_SceneNetworkObjectDataOffsets = new Dictionary<uint, long>();
                }

                if (keyPairCount > 0)
                {
                    m_SceneNetworkObjectDataOffsets.Clear();

                    InternalBuffer.Position = 0;

                    using (var writer = PooledNetworkWriter.Get(InternalBuffer))
                    {
                        for (int i = 0; i < keyPairCount; i++)
                        {
                            var key = reader.ReadUInt32Packed();
                            var count = reader.ReadInt32Packed();
                            // how many bytes to read for this scene set
                            var bytesToRead = (ulong)reader.ReadUInt32();
                            // We store off the current position of the stream as it pertains to the scene relative NetworkObjects
                            m_SceneNetworkObjectDataOffsets.Add(key, InternalBuffer.Position);
                            writer.WriteInt32Packed(count);
                            writer.ReadAndWrite(reader, (long)bytesToRead);
                        }
                    }
                }
            }

            if (SceneEventType == SceneEventTypes.C2S_SyncComplete)
            {
                CheckClientSynchronizationResults(reader);
            }

            if (SceneEventType == SceneEventTypes.S2C_ReSync)
            {
                ReadClientReSynchronizationData(reader);
            }

            if (SceneEventType == SceneEventTypes.S2C_LoadComplete || SceneEventType == SceneEventTypes.S2C_UnLoadComplete)
            {
               ReadSceneEventProgressDone(reader);
            }
        }

        /// <summary>
        /// Client Side:
        /// If there happens to be NetworkObjects in the final Event_Sync_Complete message that are no longer spawned,
        /// the server will compile a list and send back an Event_ReSync message to the client.  This is where the
        /// client handles any returned values by the server.
        /// </summary>
        /// <param name="reader"></param>
        internal void ReadClientReSynchronizationData(NetworkReader reader)
        {
            var networkObjectsToRemove = reader.ReadULongArrayPacked();

            if (networkObjectsToRemove.Length > 0)
            {
                var networkObjects = UnityEngine.Object.FindObjectsOfType<NetworkObject>();
                var networkObjectIdToNetworkObject = new Dictionary<ulong, NetworkObject>();
                foreach (var networkObject in networkObjects)
                {
                    if (!networkObjectIdToNetworkObject.ContainsKey(networkObject.NetworkObjectId))
                    {
                        networkObjectIdToNetworkObject.Add(networkObject.NetworkObjectId, networkObject);
                    }
                }

                foreach (var networkObjectId in networkObjectsToRemove)
                {
                    if (networkObjectIdToNetworkObject.ContainsKey(networkObjectId))
                    {
                        var networkObject = networkObjectIdToNetworkObject[networkObjectId];
                        networkObjectIdToNetworkObject.Remove(networkObjectId);

                        networkObject.IsSpawned = false;
                        if (m_NetworkManager.PrefabHandler.ContainsHandler(networkObject))
                        {
                            // Since this is the client side and we have missed the delete message, until the Snapshot system is in place for spawn and despawn handling
                            // we have to remove this from the list of spawned objects manually or when a NetworkObjectId is recycled the client will throw an error
                            // about the id already being assigned.
                            if (m_NetworkManager.SpawnManager.SpawnedObjects.ContainsKey(networkObjectId))
                            {
                                m_NetworkManager.SpawnManager.SpawnedObjects.Remove(networkObjectId);
                            }
                            if (m_NetworkManager.SpawnManager.SpawnedObjectsList.Contains(networkObject))
                            {
                                m_NetworkManager.SpawnManager.SpawnedObjectsList.Remove(networkObject);
                            }
                            NetworkManager.Singleton.PrefabHandler.HandleNetworkPrefabDestroy(networkObject);
                        }
                        else
                        {
                            UnityEngine.Object.DestroyImmediate(networkObject.gameObject);
                        }
                    }
                }
            }
        }

        /// <summary>
        /// Server Side:
        /// If there happens to be NetworkObjects in the final Event_Sync_Complete message that are no longer spawned,
        /// the server will compile a list and send back an Event_ReSync message to the client.
        /// </summary>
        /// <param name="writer"></param>
        internal void WriteClientReSynchronizationData(NetworkWriter writer)
        {
            //Write how many objects need to be removed
            writer.WriteULongArrayPacked(m_NetworkObjectsToBeRemoved.ToArray());
        }

        /// <summary>
        /// Server Side:
        /// Determines if the client needs to be slightly re-synchronized if during the deserialization
        /// process the server finds NetworkObjects that the client still thinks are spawned.
        /// </summary>
        /// <returns></returns>
        internal bool ClientNeedsReSynchronization()
        {
            return (m_NetworkObjectsToBeRemoved.Count > 0);
        }

        /// <summary>
        /// Server Side:
        /// Determines if the client needs to be re-synchronized if during the deserialization
        /// process the server finds NetworkObjects that the client still thinks are spawned but
        /// have since been despawned.
        /// </summary>
        /// <param name="reader"></param>
        internal void CheckClientSynchronizationResults(NetworkReader reader)
        {
            m_NetworkObjectsToBeRemoved.Clear();
            var networkObjectIdCount = reader.ReadUInt32Packed();
            for (int i = 0; i < networkObjectIdCount; i++)
            {
                var networkObjectId = (ulong)reader.ReadUInt32Packed();
                if (!m_NetworkManager.SpawnManager.SpawnedObjects.ContainsKey(networkObjectId))
                {
                    m_NetworkObjectsToBeRemoved.Add(networkObjectId);
                }
            }
        }

        /// <summary>
        /// Client Side:
        /// During the deserialization process of the servers Event_Sync, the client builds a list of
        /// all NetworkObjectIds that were spawned.  Upon responding to the server with the Event_Sync_Complete
        /// this list is included for the server to review over and determine if the client needs a minor resynchronization
        /// of NetworkObjects that might have been despawned while the client was processing the Event_Sync.
        /// </summary>
        /// <param name="writer"></param>
        internal void WriteClientSynchronizationResults(NetworkWriter writer)
        {
            //Write how many objects were spawned
            writer.WriteUInt32Packed((uint)m_NetworkObjectsSync.Count);
            foreach (var networkObject in m_NetworkObjectsSync)
            {
                writer.WriteUInt32Packed((uint)networkObject.NetworkObjectId);
            }
        }

        /// <summary>
        /// Client Side:
        /// During the processing of a server sent Event_Sync, this method will be called for each scene once
        /// it is finished loading.  The client will also build a list of NetworkObjects that it spawned during
        /// this process which will be used as part of the Event_Sync_Complete response.
        /// </summary>
        /// <param name="sceneId"></param>
        /// <param name="networkManager"></param>
        internal void SynchronizeSceneNetworkObjects(uint sceneId, NetworkManager networkManager)
        {
            if (m_SceneNetworkObjectDataOffsets.ContainsKey(sceneId))
            {
                // Point to the appropriate offset
                InternalBuffer.Position = m_SceneNetworkObjectDataOffsets[sceneId];

                using (var reader = PooledNetworkReader.Get(InternalBuffer))
                {
                    // Process all NetworkObjects for this scene
                    var newObjectsCount = reader.ReadInt32Packed();

                    for (int i = 0; i < newObjectsCount; i++)
                    {
                        var spawnedNetworkObject = NetworkObject.DeserializeSceneObject(InternalBuffer, reader, networkManager);
                        if (!m_NetworkObjectsSync.Contains(spawnedNetworkObject))
                        {
                            m_NetworkObjectsSync.Add(spawnedNetworkObject);
                        }
                    }
                }

                // Remove each entry after it is processed so we know when we are done
                m_SceneNetworkObjectDataOffsets.Remove(sceneId);
            }
        }

        /// <summary>
        /// Writes the all clients loaded or unloaded completed and timed out lists
        /// </summary>
        /// <param name="writer"></param>
        internal void WriteSceneEventProgressDone(NetworkWriter writer)
        {
            writer.WriteUInt16Packed((ushort)ClientsCompleted.Count);
            foreach (var clientId in ClientsCompleted)
            {
                writer.WriteUInt64Packed(clientId);
            }

            writer.WriteUInt16Packed((ushort)ClientsTimedOut.Count);
            foreach (var clientId in ClientsTimedOut)
            {
                writer.WriteUInt64Packed(clientId);
            }
        }

        /// <summary>
        /// Reads the all clients loaded or unloaded completed and timed out lists
        /// </summary>
        /// <param name="reader"></param>
        internal void ReadSceneEventProgressDone(NetworkReader reader)
        {
            var completedCount = reader.ReadUInt16Packed();
            ClientsCompleted = new List<ulong>();
            for (int i = 0; i < completedCount; i++)
            {
                ClientsCompleted.Add(reader.ReadUInt64Packed());
            }

            var timedOutCount = reader.ReadUInt16Packed();
            ClientsTimedOut = new List<ulong>();
            for (int i = 0; i < timedOutCount; i++)
            {
                ClientsTimedOut.Add(reader.ReadUInt64Packed());
            }
        }

        /// <summary>
        /// Used to store data during an asynchronous scene loading event
        /// </summary>
        /// <param name="stream"></param>
        internal void CopyUnreadFromStream(Stream stream)
        {
            InternalBuffer.Position = 0;
            InternalBuffer.CopyUnreadFrom(stream);
            InternalBuffer.Position = 0;
        }

        /// <summary>
        /// Used to release the pooled network buffer
        /// </summary>
        public void Dispose()
        {
            if (InternalBuffer != null)
            {
                NetworkBufferPool.PutBackInPool(InternalBuffer);
                InternalBuffer = null;
            }
        }

        /// <summary>
        /// Constructor
        /// </summary>
        internal SceneEventData(NetworkManager networkManager)
        {
            m_NetworkManager = networkManager;
            InternalBuffer = NetworkBufferPool.GetBuffer();
        }
    }
}
