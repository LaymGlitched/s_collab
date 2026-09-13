namespace Editor.TeamSync;

/// <summary>
/// Common transport interface supporting WebSocket server, WebSocket client, and in-memory loopback.
/// </summary>
public interface ITeamSyncTransport : IDisposable
{
	event Action<TeamSyncEnvelope> OnMessageReceived;
	event Action<string> OnPeerConnected;
	event Action<string> OnPeerDisconnected;
	event Action<string> OnStatusChanged;
	event Action<string> OnError;

	bool IsRunning { get; }
	bool IsHost { get; }
	string LocalPeerId { get; }
	string StatusText { get; }

	Task StartAsync();
	Task StopAsync();
	Task SendAsync( TeamSyncEnvelope envelope );
	Task BroadcastAsync( TeamSyncEnvelope envelope, string exceptPeerId = null );
}
