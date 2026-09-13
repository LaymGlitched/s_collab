namespace Editor.TeamSync;

/// <summary>
/// Supported message types for Team Sync collaboration protocol.
/// </summary>
public enum TeamSyncMessageType
{
	Unknown = 0,
	Hello,             // Client -> Host (identifies self)
	Welcome,           // Host -> Client (session state + snapshot)
	PeerJoined,        // Host -> All (a new peer connected)
	PeerLeft,          // Host -> All (a peer disconnected)
	Heartbeat,         // Keepalive ping/pong
	CameraUpdate,      // Camera transform update
	SelectionUpdate,   // Selected object IDs update
	LockRequest,       // Request to acquire or release object lock
	LockUpdate,        // Broadcast of current lock table
	SceneDelta,        // Real-time scene graph change
	SceneSnapshot,     // Full scene snapshot for catch-up
	SceneSaveNotice,   // Notification that scene was saved to disk by host
	HostMigration,     // Notification that host migrated to a new peer
	FileSync           // Live project file/asset synchronization
}

/// <summary>
/// Envelope message wrapper sent over WebSocket or Loopback.
/// </summary>
public sealed class TeamSyncEnvelope
{
	[JsonPropertyName( "t" )]
	public TeamSyncMessageType Type { get; set; }

	[JsonPropertyName( "s" )]
	public string SenderId { get; set; } = string.Empty;

	[JsonPropertyName( "ts" )]
	public long Timestamp { get; set; } = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

	[JsonPropertyName( "p" )]
	public string PayloadJson { get; set; } = string.Empty;

	public static TeamSyncEnvelope Create<T>( TeamSyncMessageType type, string senderId, T payload )
	{
		return new TeamSyncEnvelope
		{
			Type = type,
			SenderId = senderId,
			Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
			PayloadJson = JsonSerializer.Serialize( payload )
		};
	}

	public T GetPayload<T>()
	{
		if ( string.IsNullOrEmpty( PayloadJson ) )
			return default;

		return JsonSerializer.Deserialize<T>( PayloadJson );
	}

	public string Serialize() => JsonSerializer.Serialize( this );

	public static TeamSyncEnvelope Deserialize( string json )
	{
		if ( string.IsNullOrWhiteSpace( json ) ) return null;
		try
		{
			return JsonSerializer.Deserialize<TeamSyncEnvelope>( json );
		}
		catch
		{
			return null;
		}
	}
}

public sealed class HelloPayload
{
	public string PeerId { get; set; }
	public string PersonaName { get; set; }
	public ulong SteamId { get; set; }
	public string ColorHex { get; set; }
}

public sealed class WelcomePayload
{
	public string AssignedPeerId { get; set; }
	public string HostPeerId { get; set; }
	public string ActiveSceneName { get; set; }
	public List<PeerInfo> Peers { get; set; } = new();
	public Dictionary<string, string> ActiveLocks { get; set; } = new(); // GameObjectId -> PeerId
	public string SceneJson { get; set; } // Full snapshot JSON
}

public sealed class PeerInfo
{
	public string PeerId { get; set; }
	public string PersonaName { get; set; }
	public ulong SteamId { get; set; }
	public string ColorHex { get; set; }
	public bool IsHost { get; set; }
	public Vector3 CameraPosition { get; set; }
	public Angles CameraAngles { get; set; }
	public List<string> SelectedObjectIds { get; set; } = new();
	public long LastSeenTimestamp { get; set; }
}

public sealed class CameraPayload
{
	public string PeerId { get; set; }
	public Vector3 Position { get; set; }
	public Angles Angles { get; set; }
	public float Fov { get; set; } = 80f;
}

public sealed class SelectionPayload
{
	public string PeerId { get; set; }
	public List<string> SelectedObjectIds { get; set; } = new();
}

public sealed class LockRequestPayload
{
	public string PeerId { get; set; }
	public string GameObjectId { get; set; }
	public bool Acquire { get; set; } // true = acquire, false = release
}

public sealed class LockUpdatePayload
{
	public Dictionary<string, string> Locks { get; set; } = new(); // GameObjectId -> PeerId
}

public enum SceneDeltaType
{
	CreateGameObject,
	DeleteGameObject,
	SetTransform,
	SetParent,
	SetEnabled,
	SetName,
	SetStatic,
	SetTags,
	SetNetworkMode,
	AddComponent,
	UpdateComponent,
	RemoveComponent,
	SetComponentProperty
}

public sealed class SceneDeltaPayload
{
	public SceneDeltaType DeltaType { get; set; }
	public string TargetGameObjectId { get; set; }
	public string TargetParentId { get; set; }
	public string Name { get; set; }
	public bool Enabled { get; set; }
	public Vector3 Position { get; set; }
	public Rotation Rotation { get; set; }
	public Vector3 Scale { get; set; } = Vector3.One;
	public bool? IsStatic { get; set; }
	public List<string> Tags { get; set; }
	public int? NetworkMode { get; set; }
	public bool? Networked { get; set; }
	public bool? NetworkInterpolation { get; set; }
	public string PrefabSource { get; set; }
	public string ComponentId { get; set; }
	public string ComponentType { get; set; }
	public string ComponentJson { get; set; }
	public string PropertyName { get; set; }
	public string PropertyValueJson { get; set; }
	public string SerializedGameObjectJson { get; set; }
}

public sealed class FileChunkPayload
{
	public string RelativePath { get; set; } = string.Empty;
	public string Base64Data { get; set; } = string.Empty;
	public string FileHash { get; set; } = string.Empty;
	public bool IsDeleted { get; set; }
}

public sealed class SceneSnapshotPayload
{
	public string SceneName { get; set; }
	public string SceneJson { get; set; }
}

public sealed class HostMigrationPayload
{
	public string PreviousHostId { get; set; }
	public string NewHostId { get; set; }
	public int Port { get; set; }
}
