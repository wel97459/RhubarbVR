﻿using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using DiscordRPC;

using LibVLCSharp.Shared;

using LiteNetLib;

using Newtonsoft.Json;

using RhubarbCloudClient.Model;

using RhuEngine.AssetSystem.RequestStructs;
using RhuEngine.DataStructure;
using RhuEngine.Datatypes;
using RhuEngine.Linker;
using RhuEngine.Managers;

using RNumerics;

using SharedModels;
using SharedModels.GameSpecific;


namespace RhuEngine.WorldObjects
{
	public sealed partial class World : IWorldObject
	{

		public bool IsNetworked { get; internal set; } = false;
		public bool IsLoadingNet { get; internal set; } = true;
		public bool WaitingForWorldStartState { get; internal set; } = true;

		public ushort MasterUser { get; set; } = 1;

		public ushort ConnectedUserCount => (ushort)Users?.Where(x => ((User)x).IsConnected || ((User)x).IsLocalUser).Count();

		public List<RelayPeer> relayServers = new();
		public List<Peer> peers = new();

		private NetManager _netManager;
		private readonly EventBasedNatPunchListener _natPunchListener = new();
		private readonly EventBasedNetListener _clientListener = new();
		public async Task StartNetworking(bool newWorld) {
			IsNetworked = true;
			if (newWorld) {
				IsDeserializing = false;
				AddLocalUser();
				await ConnectedToSession(false);
				WaitingForWorldStartState = false;
			}
			else {
				LocalUserID = 0;
				IsLoadingNet = true;
				IsDeserializing = true;
				await ConnectedToSession(true);
			}
		}

		private void SessionInfoChanged(IChangeable _) {
			SessionInfoChanged();
		}

		internal void SessionInfoChanged() {
			worldManager.OnWorldUpdateTaskBar?.Invoke();
			if (MasterUser != LocalUserID) {
				return;
			}
			if (!IsNetworked) {
				return;
			}
			//ToDO: SessionRequest
			//var sessionInfo = new SessionInfo {
			//	ActiveUsers = Users.Select(user => (((User)user)?.isPresent?.Value??false) && (((User)user).IsConnected || ((User)user).IsLocalUser)).Where((val) => val).Count(),
			//	Admins = Admins.Select((x) => Guid.Parse(((SyncRef<User>)x).Target?.userID.Value)).ToArray(),
			//	AssociatedGroup = Guid.Parse(AssociatedGroup.Value),
			//	IsHidden = IsHidden.Value,
			//	MaxUsers = MaxUserCount.Value,
			//	ThumNail = ThumNail.Value,
			//	SessionTags = SessionTags,
			//	SessionAccessLevel = AccessLevel.Value,
			//	SessionName = SessionName.Value,
			//	NormalizedSessionName = SessionName.Value.Normalize(),
			//	SessionId = Guid.Parse(SessionID.Value)
			//};
			//var sessionConnection = new SessionCreation {
			//	SessionInfo = sessionInfo,
			//	UserConnectionInfo = null,
			//	ForceJoin = Array.Empty<Guid>()
			//};
			//Engine.netApiManager.SendDataToSocked(new SessionRequest { RequestType = RequestType.UpdateSession, RequestData = JsonConvert.SerializeObject(sessionConnection), ID = sessionInfo.SessionId });
		}

		public bool IsJoiningSession { get; private set; }

		private async Task ConnectedToSession(bool joiningSession) {
			IsLoadingNet = true;
			IsJoiningSession = joiningSession;
			LoadNatManager();
			try {
				var Pings = new Dictionary<string, int>();
				var servers = await Engine.netApiManager.Client.GetRelayHoleServers();
				foreach (var item in servers) {
					var pingSender = new Ping();
					var e = pingSender.Send(item, 5000);
					if ((e?.Status ?? IPStatus.Unknown) == IPStatus.Success) {
						Pings.Add(item, (int)e.RoundtripTime);
					}
					else {
						Pings.Add(item, 99999);
					}
				}
				foreach (var item in Pings) {
					RLog.Info($"Server {item.Key} Ping:{item.Value}");
				}
				if (!Guid.TryParse(ThumNail.Value, out var thumNail)) {
					thumNail = Guid.Empty;
				}

				var userConnection = new UserConnectionInfo {
					ConnectionType = Engine.MainSettings.NetworkingSettings.PreferredConnectionType,
					ServerPingLevels = Pings,
					Data = null
				};
				if(userConnection.ConnectionType == ConnectionType.Direct) {
					userConnection.Data = $"{Engine.MainSettings.NetworkingSettings.PublicIP}:{_netManager.LocalPort}";
				}


				if (!joiningSession) {
					var newGUId = Guid.NewGuid();
					SessionID.Value = newGUId.ToString();
					var sessionConnection = new SessionCreation {
						TempSessionID = newGUId,
						UserConnectionInfo = userConnection,
						SessionName = SessionName.Value,
						SessionTags = SessionTags,
						SessionAccessLevel = AccessLevel.Value,
						MaxUsers = MaxUserCount.Value,
						IsHidden = IsHidden.Value,
						ThumNail = thumNail,
						WorldID = WorldID.Value,
						IsAssociatedToGroup = Guid.TryParse(AssociatedGroup.Value, out var gorupID),
						AssociatedGroup = gorupID,
					};
					await Engine.netApiManager.Client.CreateSession(sessionConnection);

				}
				else {
					var sessionConnection = new JoinSession {
						SessionID = Guid.Parse(SessionID.Value),
						UserConnectionInfo = userConnection,
					};
					await Engine.netApiManager.Client.JoinSession(sessionConnection);
				}
				IsLoadingNet = false;
			}
			catch (Exception ex) {
				LoadMsg = "Failed to Connected To Session " + ex;
			}
		}

		private string KEY => $"Rhubarb_{worldManager.Engine.netApiManager.Client.ClientCompatibility}";

		public readonly ConcurrentDictionary<string, bool> NatIntroductionSuccessIsGood = new();
		public readonly ConcurrentDictionary<string, NetPeer> NatConnection = new();
		public readonly ConcurrentDictionary<string, Guid> NatUserIDS = new();

		private void FindNewMaster() {
			for (var i = 0; i < Users.Count; i++) {
				var user = Users[i];
				if (user.IsConnected || user.IsLocalUser) {
					MasterUser = (ushort)(i + 1);
					break;
				}
			}
		}

		[System.Diagnostics.CodeAnalysis.SuppressMessage("Style", "IDE0060:Remove unused parameter", Justification = "<Pending>")]
		private void PeerDisconect(Peer peer) {
			FindNewMaster();
		}

		private bool LoadNatManager() {
			_natPunchListener.NatIntroductionSuccess += (point, addrType, token) => {
				RLog.Info($"NatIntroductionSuccess {point}  {addrType}  {token}");
				NatIntroductionSuccessIsGood[token] = true;
				var peer = _netManager.Connect(point, token + '~' + KEY);
				if (peer is null) {
					RLog.Info($"NatIntroduction Failed");
					return;
				}
				var reload = peer.Tag is bool ta && ta;
				peer.Tag = NatUserIDS[token];
				NatConnection.TryAdd(token, peer);
				if (reload) {
					PeerConnected(peer);
				}
			};

			_natPunchListener.NatIntroductionRequest += (point, addrType, token) => {
				RLog.Info($"NatIntroductionRequest {point}  {addrType}  {token}");
				NatIntroductionSuccessIsGood.TryAdd(token, false);
			};

			_clientListener.PeerConnectedEvent += PeerConnected;

			_clientListener.ConnectionRequestEvent += request => {
				try {
					var key = request.Data.GetString();
					RLog.Info($"ConnectionRequestEvent Key: {key}");
					if (key.Contains('~')) {
						var e = key.Split('~');
						if (e[1] == KEY) {
							RLog.Info($"ConnectionRequestEvent Accepted");
							var peer = request.Accept();
							var reload = peer.Tag is bool ta && ta;
							peer.Tag = NatUserIDS[e[0]];
							NatIntroductionSuccessIsGood.TryAdd(e[0], true);
							NatConnection.TryAdd(e[0], peer);
							if (reload) {
								PeerConnected(peer);
							}
						}
						else {
							RLog.Info($"ConnectionRequestEvent Reject Key invalied");
							request.Reject();
						}
					}
					else {
						request.AcceptIfKey(KEY);
					}
				}
				catch {
					request.Reject();
				}
			};

			_clientListener.NetworkReceiveEvent += ClientListener_NetworkReceiveEvent;
			_clientListener.NetworkLatencyUpdateEvent += ClientListener_NetworkLatencyUpdateEvent;

			_clientListener.PeerDisconnectedEvent += (peer, disconnectInfo) => {
				if (peer.Tag is Peer rpeer) {
					PeerDisconect(rpeer);
				}
				else if (peer.Tag is RelayPeer repeer) {
					if(disconnectInfo.Reason == DisconnectReason.ConnectionFailed) {
						if (!repeer._hasUsedFallback) {
							repeer._hasUsedFallback = true;
							RLog.Info($"Relay Failed trying local connection");
							var newpeer = repeer.NetPeer = _netManager.Connect("192.168.0.97", 7857, repeer._connectionData);
							newpeer.Tag = repeer;
							return;
						}
					}
					relayServers.Remove(repeer);
					foreach (var item in repeer.peers) {
						PeerDisconect(item);
					}
				}
				RLog.Info($"PeerDisconnected Type {peer.Tag}");
				RLog.Info($"PeerDisconnected: " + disconnectInfo.Reason);
				if (disconnectInfo.AdditionalData.AvailableBytes > 0) {
					RLog.Info("Disconnect data: " + disconnectInfo.AdditionalData.GetInt());
				}
			};

			_netManager = new NetManager(_clientListener) {
				IPv6Mode = IPv6Mode.SeparateSocket,
				NatPunchEnabled = true,
				EnableStatistics = true,
				DisconnectTimeout = 30000,
				UpdateTime = 4,
				//0 is main
				//1 is syncStreams
				//2 is assetPackeds
				ChannelsCount = 3,
				AutoRecycle = true,
				//Made unsync to make run faster
				UnsyncedEvents = true,
			};
			_netManager.NatPunchModule.Init(_natPunchListener);
			var targetPort = 0;
			if (Engine.MainSettings.NetworkingSettings.PreferredConnectionType == ConnectionType.Direct) {
				targetPort = Engine.MainSettings.NetworkingSettings.StartPortRange;
				if(Engine.MainSettings.NetworkingSettings.EndPortRange <= Engine.MainSettings.NetworkingSettings.StartPortRange) {
					RLog.Err(LoadMsg = "End Port Range needs to be more than StartPortRange");
					return false;
				}
			}
			if(targetPort != 0) {
				while(targetPort <= Engine.MainSettings.NetworkingSettings.StartPortRange) {
					RLog.Info(LoadMsg = $"Trying to start world networking Port {targetPort}");
					if (_netManager.Start(targetPort)) {
						RLog.Info(LoadMsg = $"World networking Port {targetPort}");
						break;
					}
				}
				return _netManager.IsRunning;
			}
			else {
				if (!_netManager.Start(targetPort)) {
					RLog.Err(LoadMsg = "Failed to start world networking");
					return false;
				}

			}
			return true;
		}

		private void ClientListener_NetworkLatencyUpdateEvent(NetPeer peer, int latency) {
			if (peer.Tag is Peer rupeer) {
				rupeer.latency = latency;
			}
			else if (peer.Tag is RelayPeer repeer) {
				repeer.UpdateLatency(latency);
			}
		}

		public NetStatistics NetStatistics => _netManager?.Statistics;

		private bool _waitingForUsers = false;

		private void ProcessPackedData(DataNodeGroup dataGroup, DeliveryMethod deliveryMethod, Peer peer) {
			if (WaitingForWorldStartState) {
				if (deliveryMethod == DeliveryMethod.Unreliable) {
					return;
				}
				if (_waitingForUsers) {
					return;
				}
				LoadMsg = "Waiting For World Start State";
				var worldData = dataGroup.GetValue("WorldData");
				if (worldData is null) {
					return;
				}
				else {
					_waitingForUsers = true;
					Task.Run(async () => {
						try {
							// Wait for everyone
							RLog.Info(LoadMsg = "Waiting on all users connections");
							while (ActiveConnections.Count > 0) {
								await Task.Delay(100);
							}
							_worldObjects.Clear();
							var deserializer = new SyncObjectDeserializerObject(false);
							Deserialize((DataNodeGroup)worldData, deserializer);
							LocalUserID = (ushort)(Users.Count + 1);
							RLog.Info(LoadMsg = "World state loaded");
							foreach (var peer1 in _netManager.ConnectedPeerList) {
								if (peer1.Tag is Peer contpeer) {
									LoadUserIn(contpeer);
								}
							}
							FindNewMaster();
							AddLocalUser();
							foreach (var item in deserializer.onLoaded) {
								item?.Invoke();
							}
							RLog.Info(LoadMsg = "DoneDeserlizing");
							IsDeserializing = false;
							WaitingForWorldStartState = false;
						}
						catch (Exception ex) {
							RLog.Err("Failed to load world state" + ex);
							LoadMsg = "Failed to load world state" + ex;
						}
					});
				}
			}
			else {
				var target = (DataNode<NetPointer>)dataGroup.GetValue("Pointer");
				if (target == null) {
					return;
				}
				try {
					lock (_networkedObjects) {
						if (_networkedObjects.ContainsKey(target.Value)) {
							_networkedObjects[target.Value].Received(peer, dataGroup.GetValue("Data"));
						}
						else {
							if (deliveryMethod == DeliveryMethod.ReliableOrdered && peer.User is not null) {
								RLog.Err($"Failed to Process NetData target:{target.Value.HexString()} Error: _networkedObjects Not loaded");
							}
						}
					}
				}
				catch (Exception ex) {
#if DEBUG
					if (deliveryMethod == DeliveryMethod.ReliableOrdered && peer.User is not null) {
						RLog.Err($"Failed to Process NetData target:{target.Value.HexString()} Error:{ex}");
					}
#endif
				}
			}
		}

		public interface INetPacked : IRelayNetPacked
		{
		}

		public static class NetPacked
		{
			public static void Serlize(BinaryWriter binaryWriter, INetPacked netPacked) {
				if (netPacked is BlockStore) {
					binaryWriter.Write((byte)11);
				}
				else if (netPacked is RequestAsset) {
					binaryWriter.Write((byte)12);
				}
				else if (netPacked is AssetResponse) {
					binaryWriter.Write((byte)13);
				}
				else if (netPacked is PremoteAsset) {
					binaryWriter.Write((byte)14);
				}
				else {
					throw new Exception("Don't know");
				}
				netPacked.Serlize(binaryWriter);
			}
			public static IRelayNetPacked DeSerlize(BinaryReader binaryReader) {
				var type = binaryReader.ReadByte();
				if (type <= 10) {
					return RelayNetPacked.DeSerlize(binaryReader, type);
				}
				INetPacked netPacked;
				if (type == 11) {
					netPacked = new BlockStore();
				}
				else if (type == 12) {
					netPacked = new RequestAsset();
				}
				else if (type == 13) {
					netPacked = new AssetResponse();
				}
				else if (type == 14) {
					netPacked = new PremoteAsset();
				}
				else {
					throw new Exception("Don't know");
				}
				netPacked.DeSerlize(binaryReader);
				return netPacked;
			}
		}


		private void ClientListener_NetworkReceiveEvent(NetPeer peer, NetPacketReader reader,byte channel, DeliveryMethod deliveryMethod) {
			if (IsDisposed) {
				return;
			}
			try {
				if (peer.Tag is Guid id) {
					RLog.Err($"Did not connect user yet {id}");
					PeerConnected(peer);
				}
				var data = reader.GetRemainingBytes();
				using var memStream = new MemoryStream(data);
				using var readere = new BinaryReader(memStream);
				var packed = NetPacked.DeSerlize(readere);
				if (peer.Tag is Peer) {
					var tag = peer.Tag as Peer;
					if (deliveryMethod == DeliveryMethod.Unreliable) {
						if (packed is StreamDataPacked streamDataPacked) {
							ProcessPackedData((DataNodeGroup)new DataReader(streamDataPacked.Data).Data, deliveryMethod, tag);
						}
						else if (packed is INetPacked rawDataPacked) {
							if (rawDataPacked is BlockStore keyValuePairs) {
								ProcessPackedData((DataNodeGroup)new DataReader(keyValuePairs).Data, deliveryMethod, tag);
							}
							else if (rawDataPacked is IAssetRequest assetRequest) {
								AssetResponses(assetRequest, tag);
							}
							else {
								throw new Exception($"Not none {rawDataPacked?.GetType()?.GetFormattedName()}");
							}
						}
						else {
							throw new Exception("Uknown Data from User");
						}
					}
					else {
						if (packed is INetPacked rawDataPacked) {
							if (rawDataPacked is BlockStore keyValuePairs) {
								ProcessPackedData((DataNodeGroup)new DataReader(keyValuePairs).Data, deliveryMethod, tag);
							}
							else if (rawDataPacked is IAssetRequest assetRequest) {
								AssetResponses(assetRequest, tag);
							}
							else {
								throw new Exception($"Not none {rawDataPacked?.GetType()?.GetFormattedName()}");
							}
						}
						else if (packed is StreamDataPacked streamDataPacked) {
							ProcessPackedData((DataNodeGroup)new DataReader(streamDataPacked.Data).Data, deliveryMethod, tag);
						}
						else {
							throw new Exception("Uknown Data from User");
						}
					}

				}
				else if (peer.Tag is RelayPeer) {
					var tag = peer.Tag as RelayPeer;
					if (packed is DataPacked packede) {
						using var memStreame = new MemoryStream(packede.Data);
						using var readeree = new BinaryReader(memStreame);
						var packeder = NetPacked.DeSerlize(readeree);
						if (deliveryMethod == DeliveryMethod.Unreliable) {
							if (packeder is StreamDataPacked streamDataPacked) {
								ProcessPackedData((DataNodeGroup)new DataReader(streamDataPacked.Data).Data, deliveryMethod, tag[packede.Id]);
							}
							else if (packeder is INetPacked rawDataPacked) {
								if (rawDataPacked is BlockStore keyValuePairs) {
									ProcessPackedData((DataNodeGroup)new DataReader(keyValuePairs).Data, deliveryMethod, tag[packede.Id]);
								}
								else if (rawDataPacked is IAssetRequest assetRequest) {
									AssetResponses(assetRequest, tag[packede.Id]);
								}
								else {
									throw new Exception($"Uknown Data from relay Unreliable {packeder} {packeder?.GetType()}");
								}
							}
							else {
								throw new Exception($"Uknown Data from relay Unreliable {packeder} {packeder?.GetType()}");
							}
						}
						else {
							if (packeder is INetPacked rawDataPacked) {
								if (rawDataPacked is BlockStore keyValuePairs) {
									ProcessPackedData((DataNodeGroup)new DataReader(keyValuePairs).Data, deliveryMethod, tag[packede.Id]);
								}
								else if (rawDataPacked is IAssetRequest assetRequest) {
									AssetResponses(assetRequest, tag[packede.Id]);
								}
								else {
									throw new Exception($"Uknown Data from relay Reliable {packeder} {packeder?.GetType()}");
								}
							}
							else if (packeder is StreamDataPacked streamDataPacked) {
								ProcessPackedData((DataNodeGroup)new DataReader(streamDataPacked.Data).Data, deliveryMethod, tag[packede.Id]);
							}
							else {
								throw new Exception($"Uknown Data from relay Reliable {packeder} {packeder?.GetType()}");
							}
						}
					}
					else if (packed is OtherUserLeft otherUserLeft) {
						var rpeer = tag.peers[otherUserLeft.id];
						tag.peers.Remove(rpeer);
						rpeer.KillRelayConnection();
						PeerDisconect(rpeer);
					}
					else {
						throw new Exception("realy packed not known");
					}

				}
				else {
					throw new Exception("Peer is not known");
				}
			}
			catch (Exception ex) {
				RLog.Err($"Failed to proccess packed Error {ex}");
			}
		}

		public void ProcessUserConnection(Peer peer) {
			RLog.Info($"User connected {peer.ID}({peer.UserID})");
			peers.Add(peer);
			if (IsLoading) {
				return;
			}
			if (LocalUserID == MasterUser) {
				RLog.Info($"Sending initial world state to a new user {peer.ID}({peer.UserID})");
				var dataGroup = new DataNodeGroup();
				dataGroup.SetValue("WorldData", Serialize(new SyncObjectSerializerObject(true)));
				using var memstream = new MemoryStream();
				using var reader = new BinaryWriter(memstream);
				NetPacked.Serlize(reader, new DataSaver(dataGroup).Store);
				peer.Send(memstream.ToArray(), DeliveryMethod.ReliableOrdered);
			}
			else {
				RLog.Info($"Local Was {LocalUserID} {MasterUser}");
			}
			LoadUserIn(peer);
			if(IsLoading || WaitingForWorldStartState) {
				return;
			}
			FindNewMaster();
		}

		private void PeerConnected(NetPeer peer) {
			if (peer.Tag is Peer) {
				RLog.Info("Peer alreadyLoaded");
				return;
			}
			RLog.Info("Peer connected");
			if (peer.EndPoint.Address.ToString().Contains("127.0.0.1") && peer.Tag is not RelayPeer) {  // this is to make debuging essayer
				return;
			}
			if (peer.Tag is RelayPeer relayPeer) {
				RLog.Info("Peer was relay server");
				relayServers.Add(relayPeer);
				relayPeer.OnConnect();
			}
			else if (peer.Tag is Guid @id) {
				if (@id == Engine.netApiManager.Client?.User?.Id) {
					RLog.Err("Normal Peer id was self");
				}
				RLog.Info("Normal Peer Loaded");
				var newpeer = new Peer(peer, @id);
				peer.Tag = newpeer;
				ProcessUserConnection(newpeer);
			}
			else {
				RLog.Err($"Peer had no tag noidea what to do {peer.Tag} marking for reload");
				peer.Tag = true;
			}
		}

		public List<ConnectToUser> ActiveConnections = new();

		public async Task ConnectToUser(ConnectToUser user) {
			lock (ActiveConnections) {
				ActiveConnections.Add(user);
			}
			if (user is null) {
				return;
			}
			RLog.Info("Connecting to user " + user.ConnectionType.ToString() + " Token:" + user.Data + " UserID:" + user.UserID);
			LoadMsg = "Connecting to user";
			switch (user.ConnectionType) {
				case ConnectionType.Direct:
					LoadMsg = "Direct Connected to User";
					var idUri = new Uri(user.Data);
					var dpeer = _netManager.Connect(idUri.Host, idUri.Port, KEY);
					var reload = dpeer.Tag is bool ta && ta;
					dpeer.Tag = user.UserID;
					lock (ActiveConnections) {
						ActiveConnections.Remove(user);
					}
					if (reload) {
						PeerConnected(dpeer);
					}
					break;
				case ConnectionType.HolePunch:
					try {
						LoadMsg = "Trying to HolePunch to User";
						var peerCount = _netManager.ConnectedPeersCount;
						NatUserIDS.TryAdd(user.Data, user.UserID);
						_netManager.NatPunchModule.SendNatIntroduceRequest(user.Server, 7856, user.Data);
						for (var i = 0; i < 6; i++) {
							if (NatIntroductionSuccessIsGood.TryGetValue(user.Data, out var evalue) && evalue) {
								if (NatConnection.TryGetValue(user.Data, out var peer)) {
									if ((peer?.ConnectionState ?? ConnectionState.Disconnected) == ConnectionState.Connected) {
										break;
									}
								}
							}
							LoadMsg = $"HolePunch Try {i}";
							//Like this so i can add update Msgs
							await Task.Delay(1000);
						}
						if (NatIntroductionSuccessIsGood.TryGetValue(user.Data, out var value) && value) {
							if (NatConnection.TryGetValue(user.Data, out var peer)) {
								if ((peer?.ConnectionState ?? ConnectionState.Disconnected) != ConnectionState.Connected) {
									try {
										peer?.Disconnect();
									}
									catch {
										return;
									}
									RLog.Info(LoadMsg = "Failed To Hole Punch now using relay");
									RelayConnect(user);
									lock (ActiveConnections) {
										ActiveConnections.Remove(user);
									}
								}
								else {
									RLog.Info(LoadMsg = "HolePunch succeeded");
									lock (ActiveConnections) {
										ActiveConnections.Remove(user);
									}
								}
							}
							else {
								RLog.Info(LoadMsg = "Failed To Hole Punch now using relay");
								RelayConnect(user);
								lock (ActiveConnections) {
									ActiveConnections.Remove(user);
								}
							}
						}
						else {
							RLog.Info(LoadMsg = "Failed To Hole Punch now using relay");
							RelayConnect(user);
							lock (ActiveConnections) {
								ActiveConnections.Remove(user);
							}
						}
						NatIntroductionSuccessIsGood.TryRemove(user.Data, out _);
						NatConnection.TryRemove(user.Data, out _);
						NatUserIDS.TryRemove(user.Data, out _);
					}
					catch (Exception e) {
						RLog.Err($"Excerption when trying to hole punch {e}");
						return;
					}
					break;
				case ConnectionType.Relay:
					LoadMsg = "Relay connecting to user";
					NatUserIDS.TryAdd(user.Data, user.UserID);
					RelayConnect(user);
					lock (ActiveConnections) {
						ActiveConnections.Remove(user);
					}
					break;
				default:
					break;
			}
			LoadMsg = "Waiting for world state";

		}

		private void RelayConnect(ConnectToUser user) {
			RLog.Info($"Trying Relay Server {user.Server}");
			try {
				var peer = _netManager.Connect(user.Server, 7857, user.Data);
				if (peer.Tag is not null) {
					RLog.Info(LoadMsg = "Adding another Relay Client");
					var relay = peer.Tag as RelayPeer;
					relay.LoadNewPeer(user);
				}
				else {
					RLog.Info(LoadMsg = "Start New Relay Server");
					var relay = new RelayPeer(peer, this, user.UserID) {
						_connectionData = user.Data
					};
					peer.Tag = relay;
				}
			}
			catch (Exception e) {
				RLog.Err(LoadMsg = "Relay Connect Error:" + e.ToString());
				throw new Exception("Failed to use relay");
			}
		}

		public void BroadcastDataToAll(IWorldObject target, IDataNode data, DeliveryMethod deliveryMethod) {
			if (target.IsRemoved) {
				return;
			}
			if (_netManager is null) {
				return;
			}
			if (IsLoading) {
				return;
			}
			if (target.Pointer.GetOwnerID() == 0) {
				//LocalValue
				return;
			}
			var netData = new DataNodeGroup();
			netData.SetValue("Data", data);
			netData.SetValue("Pointer", new DataNode<NetPointer>(target.Pointer));
			using var memstream = new MemoryStream();
			using var reader = new BinaryWriter(memstream);
			NetPacked.Serlize(reader, new DataSaver(netData).Store);
			_netManager.SendToAll(memstream.ToArray(), 0, deliveryMethod);
		}

		public void BroadcastDataToAllStream(IWorldObject target, IDataNode data, DeliveryMethod deliveryMethod) {
			if (target.IsRemoved) {
				return;
			}
			if (_netManager is null) {
				return;
			}
			if (IsLoading) {
				return;
			}
			if (target.Pointer.GetOwnerID() == 0) {
				//LocalValue
				return;
			}
			var netData = new DataNodeGroup();
			netData.SetValue("Data", data);
			netData.SetValue("Pointer", new DataNode<NetPointer>(target.Pointer));
			using var memstream = new MemoryStream();
			using var reader = new BinaryWriter(memstream);
			RelayNetPacked.Serlize(reader, new StreamDataPacked(new DataSaver(netData).SaveStore()));
			_netManager.SendToAll(memstream.ToArray(), 1, deliveryMethod);
		}


		public void AddLocalUser() {
			LoadMsg = "Adding LocalUser";
			var Olduser = GetUserFromID(Engine.netApiManager.Client.User?.Id ?? new Guid());
			if (Olduser is null) {
				LoadMsg = "Building new LocalUser";
				ItemIndex = 176;
				LocalUserID = (ushort)(Users.Count + 1);
				RLog.Info($"Built local User with id{LocalUserID}");
				var user = Users.Add();
				user.userID.Value = (worldManager.Engine.netApiManager.Client.User?.Id ?? new Guid()).ToString();
				user.Platform.Value = Environment.OSVersion.Platform;
				user.PlatformVersion.Value = Environment.OSVersion.Version.ToString();
				user.BackendID.Value = Engine.EngineLink.BackendID;
			}
			else {
				LoadMsg = "using old LocalUser";
				LocalUserID = (ushort)(Users.IndexOf(Olduser) + 1);
				RLog.Info($"using old LocalUser with id{LocalUserID}");
				var e = from objec in _worldObjects
						where objec.Key.GetOwnerID() == LocalUserID
						orderby objec.Key._id descending
						select objec.Key;
				ItemIndex = e.First().ItemIndex() + 10;
				RLog.Info($"Last index is {ItemIndex}");

			}
		}
	}
}
