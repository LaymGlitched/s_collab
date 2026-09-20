namespace Editor.TeamSync;

/// <summary>
/// Represents a single activity log event or user chat message in the collaboration session.
/// </summary>
public sealed class ChatActivityEntry
{
	public DateTime Timestamp { get; set; } = DateTime.Now;
	public string SenderName { get; set; } = "System";
	public string SenderColor { get; set; } = "#3B82F6";
	public string Text { get; set; } = string.Empty;
	public bool IsChat { get; set; }

	/// <summary>
	/// Returns the entry formatted strictly as requested:
	/// Activity: "03:15: Laym created 'Light_Ceiling'"
	/// Chat:     "03:21: Yyk: This good?"
	/// </summary>
	public string FormattedText => $"{Timestamp:HH:mm}: {(IsChat ? $"{SenderName}: {Text}" : $"{SenderName} {Text}")}";
}

/// <summary>
/// Central manager for in-editor chat messages and real-time activity logging.
/// </summary>
public sealed class ActivityChatSystem
{
	private static ActivityChatSystem _instance;
	public static ActivityChatSystem Instance => _instance ??= new ActivityChatSystem();

	public event Action<ChatActivityEntry> OnEntryAdded;
	public event Action OnEntriesCleared;

	private readonly List<ChatActivityEntry> _entries = new();
	private readonly object _lock = new();
	private const int MaxEntries = 1000;

	public IReadOnlyList<ChatActivityEntry> Entries
	{
		get
		{
			lock ( _lock )
			{
				return _entries.ToList();
			}
		}
	}

	public void LogActivity( string senderName, string activityText, string senderColor = null )
	{
		if ( string.IsNullOrWhiteSpace( senderName ) || string.IsNullOrWhiteSpace( activityText ) ) return;

		var entry = new ChatActivityEntry
		{
			Timestamp = DateTime.Now,
			SenderName = senderName,
			SenderColor = !string.IsNullOrEmpty( senderColor ) ? senderColor : ResolveSenderColor( senderName ),
			Text = activityText,
			IsChat = false
		};

		AddEntry( entry );
	}

	public void SendChat( string message )
	{
		if ( string.IsNullOrWhiteSpace( message ) ) return;

		var manager = TeamSyncManager.Instance;
		string localName = manager?.LocalPersonaName ?? "You";
		string localColor = manager != null ? manager.LocalColor.Hex : "#3B82F6";

		var payload = new ChatMessagePayload
		{
			SenderName = localName,
			SenderColor = localColor,
			Message = message.Trim()
		};

		if ( manager != null && manager.IsSessionActive )
		{
			var env = TeamSyncEnvelope.Create( TeamSyncMessageType.ChatMessage, manager.LocalPeerId, payload );
			manager.BroadcastEnvelope( env );
		}

		var entry = new ChatActivityEntry
		{
			Timestamp = DateTime.Now,
			SenderName = localName,
			SenderColor = localColor,
			Text = payload.Message,
			IsChat = true
		};

		AddEntry( entry );
	}

	public void ReceiveChat( ChatMessagePayload payload )
	{
		if ( payload == null || string.IsNullOrWhiteSpace( payload.Message ) ) return;

		var entry = new ChatActivityEntry
		{
			Timestamp = DateTime.Now,
			SenderName = !string.IsNullOrEmpty( payload.SenderName ) ? payload.SenderName : "Collaborator",
			SenderColor = !string.IsNullOrEmpty( payload.SenderColor ) ? payload.SenderColor : "#10B981",
			Text = payload.Message.Trim(),
			IsChat = true
		};

		AddEntry( entry );
	}

	private void AddEntry( ChatActivityEntry entry )
	{
		lock ( _lock )
		{
			_entries.Add( entry );
			if ( _entries.Count > MaxEntries )
			{
				_entries.RemoveAt( 0 );
			}
		}

		OnEntryAdded?.Invoke( entry );
	}

	public void Clear()
	{
		lock ( _lock )
		{
			_entries.Clear();
		}
		OnEntriesCleared?.Invoke();
	}

	private static string ResolveSenderColor( string senderName )
	{
		var manager = TeamSyncManager.Instance;
		if ( manager == null ) return "#3B82F6";

		if ( senderName == manager.LocalPersonaName ) return manager.LocalColor.Hex;

		foreach ( var peer in manager.Collaborators.Values )
		{
			if ( peer.PersonaName == senderName )
			{
				return !string.IsNullOrEmpty( peer.ColorHex ) ? peer.ColorHex : peer.Color.Hex;
			}
		}

		return "#94A3B8";
	}
}
