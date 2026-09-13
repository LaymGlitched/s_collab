namespace Editor.TeamSync;

/// <summary>
/// Represents an active TeamSync host discovered on the local network.
/// </summary>
public sealed class DiscoveredSession
{
	public string HostAddress { get; set; }
	public int Port { get; set; }
	public string ProjectTitle { get; set; }
	public string HostPersonaName { get; set; }
	public ulong HostSteamId { get; set; }
	public int CollaboratorCount { get; set; }
	public DateTime LastSeen { get; set; }
	public string RoomCode => SessionCode.Encode( HostAddress, Port );

	public string DisplaySummary => $"{ProjectTitle} ({HostPersonaName}) - {CollaboratorCount} active";
}

/// <summary>
/// Payload broadcasted over UDP multicast/broadcast for zero-config LAN discovery.
/// </summary>
public sealed class LanBeaconPayload
{
	[JsonPropertyName( "proto" )]
	public string Protocol { get; set; } = "TeamSync_v1";

	[JsonPropertyName( "project" )]
	public string ProjectTitle { get; set; }

	[JsonPropertyName( "hostName" )]
	public string HostPersonaName { get; set; }

	[JsonPropertyName( "hostSteamId" )]
	public ulong HostSteamId { get; set; }

	[JsonPropertyName( "port" )]
	public int Port { get; set; }

	[JsonPropertyName( "peers" )]
	public int CollaboratorCount { get; set; }
}

/// <summary>
/// Broadcasts and listens for local network TeamSync sessions over UDP.
/// </summary>
public sealed class LanDiscoveryService : IDisposable
{
	private const int DiscoveryPort = 29019;
	private static LanDiscoveryService _instance;
	public static LanDiscoveryService Instance => _instance ??= new LanDiscoveryService();

	public ConcurrentDictionary<string, DiscoveredSession> DiscoveredSessions { get; } = new();
	public event Action OnSessionsChanged;

	private UdpClient _udpListener;
	private CancellationTokenSource _listenerCts;
	private CancellationTokenSource _broadcasterCts;
	private bool _isListening;
	private bool _isBroadcasting;

	private LanDiscoveryService()
	{
	}

	/// <summary>
	/// Starts listening for LAN broadcasts from other hosts.
	/// </summary>
	public void StartListening()
	{
		if ( _isListening ) return;
		_isListening = true;
		_listenerCts = new CancellationTokenSource();

		Task.Run( () => ListenLoopAsync( _listenerCts.Token ) );
	}

	/// <summary>
	/// Stops listening for LAN broadcasts.
	/// </summary>
	public void StopListening()
	{
		_isListening = false;
		_listenerCts?.Cancel();
		try
		{
			_udpListener?.Close();
			_udpListener?.Dispose();
		}
		catch { }
		_udpListener = null;
	}

	/// <summary>
	/// Starts broadcasting session beacon on LAN while hosting.
	/// </summary>
	public void StartBroadcasting( int hostPort, string projectName, string hostName, ulong hostSteamId, Func<int> getPeerCount )
	{
		StopBroadcasting();
		_isBroadcasting = true;
		_broadcasterCts = new CancellationTokenSource();

		Task.Run( () => BroadcastLoopAsync( hostPort, projectName, hostName, hostSteamId, getPeerCount, _broadcasterCts.Token ) );
	}

	/// <summary>
	/// Stops broadcasting LAN beacons.
	/// </summary>
	public void StopBroadcasting()
	{
		_isBroadcasting = false;
		_broadcasterCts?.Cancel();
	}

	private async Task BroadcastLoopAsync( int hostPort, string projectName, string hostName, ulong hostSteamId, Func<int> getPeerCount, CancellationToken ct )
	{
		using var client = new UdpClient();
		client.EnableBroadcast = true;
		var broadcastEndpoint = new IPEndPoint( IPAddress.Broadcast, DiscoveryPort );

		while ( !ct.IsCancellationRequested && _isBroadcasting )
		{
			try
			{
				var beacon = new LanBeaconPayload
				{
					ProjectTitle = projectName ?? "s&box Project",
					HostPersonaName = hostName ?? "Host",
					HostSteamId = hostSteamId,
					Port = hostPort,
					CollaboratorCount = getPeerCount?.Invoke() ?? 1
				};

				string json = JsonSerializer.Serialize( beacon );
				byte[] bytes = Encoding.UTF8.GetBytes( json );

				await client.SendAsync( bytes, bytes.Length, broadcastEndpoint );
			}
			catch
			{
				// Ignore transient broadcast socket exceptions
			}

			try
			{
				await Task.Delay( 2000, ct );
			}
			catch ( OperationCanceledException )
			{
				break;
			}
		}
	}

	private async Task ListenLoopAsync( CancellationToken ct )
	{
		try
		{
			_udpListener = new UdpClient();
			_udpListener.Client.SetSocketOption( SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true );
			_udpListener.Client.Bind( new IPEndPoint( IPAddress.Any, DiscoveryPort ) );

			while ( !ct.IsCancellationRequested && _isListening )
			{
				try
				{
					var result = await _udpListener.ReceiveAsync( ct );
					string json = Encoding.UTF8.GetString( result.Buffer );
					var beacon = JsonSerializer.Deserialize<LanBeaconPayload>( json );

					if ( beacon != null && beacon.Protocol == "TeamSync_v1" )
					{
						string senderIp = result.RemoteEndPoint.Address.ToString();
						// Convert loopback/local representation
						if ( senderIp == "127.0.0.1" || senderIp == "::1" )
						{
							senderIp = IpResolver.GetPrimaryLocalIp();
						}

						string key = $"{senderIp}:{beacon.Port}";

						// Don't show ourselves as a remote discovered session if we are the host
						if ( TeamSyncManager.Instance.IsHost && beacon.Port == ((TeamSyncServer)TeamSyncManager.Instance.Transport)?.Port )
						{
							continue;
						}

						var session = new DiscoveredSession
						{
							HostAddress = senderIp,
							Port = beacon.Port,
							ProjectTitle = beacon.ProjectTitle,
							HostPersonaName = beacon.HostPersonaName,
							HostSteamId = beacon.HostSteamId,
							CollaboratorCount = beacon.CollaboratorCount,
							LastSeen = DateTime.UtcNow
						};

						DiscoveredSessions[key] = session;
						OnSessionsChanged?.Invoke();
					}
				}
				catch ( OperationCanceledException )
				{
					break;
				}
				catch
				{
					// Transient receive error
					await Task.Delay( 200, ct );
				}

				// Periodic cleanup of stale sessions
				PruneStaleSessions();
			}
		}
		catch
		{
			// Socket bind failure or shutdown
		}
	}

	public void PruneStaleSessions()
	{
		var threshold = DateTime.UtcNow - TimeSpan.FromSeconds( 6 );
		bool removedAny = false;

		foreach ( var kvp in DiscoveredSessions )
		{
			if ( kvp.Value.LastSeen < threshold )
			{
				if ( DiscoveredSessions.TryRemove( kvp.Key, out _ ) )
				{
					removedAny = true;
				}
			}
		}

		if ( removedAny )
		{
			OnSessionsChanged?.Invoke();
		}
	}

	public void Dispose()
	{
		StopBroadcasting();
		StopListening();
		DiscoveredSessions.Clear();
	}
}
