namespace Editor.TeamSync;

/// <summary>
/// In-memory loopback transport for automated testing, simulation, and multi-client verification on a single machine.
/// Connects two endpoints directly via queues without touching network sockets.
/// </summary>
public sealed class LoopbackTransport : ITeamSyncTransport
{
	public event Action<TeamSyncEnvelope> OnMessageReceived;
	public event Action<string> OnPeerConnected;
	public event Action<string> OnPeerDisconnected;
	public event Action<string> OnStatusChanged;
	public event Action<string> OnError;

	public bool IsRunning { get; private set; }
	public bool IsHost { get; }
	public string LocalPeerId { get; }
	public string StatusText { get; private set; } = "Ready";

	public LoopbackTransport PeerEndpoint { get; private set; }

	public LoopbackTransport( string localPeerId, bool isHost = true )
	{
		LocalPeerId = localPeerId;
		IsHost = isHost;
	}

	/// <summary>
	/// Connects two loopback transports together as simulated peers.
	/// </summary>
	public static (LoopbackTransport Host, LoopbackTransport Client) CreatePair( string hostId = "host_1", string clientId = "client_1" )
	{
		var host = new LoopbackTransport( hostId, isHost: true );
		var client = new LoopbackTransport( clientId, isHost: false );

		host.PeerEndpoint = client;
		client.PeerEndpoint = host;

		return (host, client);
	}

	public Task StartAsync()
	{
		IsRunning = true;
		StatusText = IsHost ? "Loopback Hosting" : "Loopback Connected";
		OnStatusChanged?.Invoke( StatusText );

		if ( PeerEndpoint != null && PeerEndpoint.IsRunning )
		{
			OnPeerConnected?.Invoke( PeerEndpoint.LocalPeerId );
			PeerEndpoint.OnPeerConnected?.Invoke( LocalPeerId );
		}

		return Task.CompletedTask;
	}

	public Task StopAsync()
	{
		IsRunning = false;
		StatusText = "Loopback Disconnected";
		OnStatusChanged?.Invoke( StatusText );

		if ( PeerEndpoint != null )
		{
			PeerEndpoint.OnPeerDisconnected?.Invoke( LocalPeerId );
		}

		return Task.CompletedTask;
	}

	public Task SendAsync( TeamSyncEnvelope envelope )
	{
		if ( !IsRunning || envelope == null || PeerEndpoint == null || !PeerEndpoint.IsRunning )
			return Task.CompletedTask;

		try
		{
			// Clone via serialization to simulate true network serialization boundary
			string json = envelope.Serialize();
			var clone = TeamSyncEnvelope.Deserialize( json );

			// Asynchronously dispatch to avoid synchronous reentrancy
			Task.Run( () =>
			{
				PeerEndpoint.OnMessageReceived?.Invoke( clone );
			} );
		}
		catch ( Exception ex )
		{
			OnError?.Invoke( ex.Message );
		}

		return Task.CompletedTask;
	}

	public Task BroadcastAsync( TeamSyncEnvelope envelope, string exceptPeerId = null )
	{
		return SendAsync( envelope );
	}

	public void Dispose()
	{
		_ = StopAsync();
	}
}
