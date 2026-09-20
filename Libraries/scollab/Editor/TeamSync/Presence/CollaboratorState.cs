namespace Editor.TeamSync;

/// <summary>
/// Represents the live state of a connected collaborator in the session.
/// </summary>
public sealed class CollaboratorState
{
	public string PeerId { get; set; }
	public string PersonaName { get; set; } = "Collaborator";
	public ulong SteamId { get; set; }
	public string ColorHex { get; set; } = "#3B82F6";
	public Color Color { get; set; } = Color.Parse( "#3B82F6" ) ?? Color.Blue;
	public bool IsHost { get; set; }
	public Vector3 CameraPosition { get; set; }
	public Angles CameraAngles { get; set; }
	public float CameraFov { get; set; } = 80f;
	public Vector3 DisplayPosition { get; set; }
	public Angles DisplayAngles { get; set; }
	private bool _hasInitialPosition;
	public HashSet<string> SelectedObjectIds { get; } = new();
	public DateTime LastSeen { get; set; } = DateTime.UtcNow;

	public bool IsActive => (DateTime.UtcNow - LastSeen).TotalSeconds < 30;

	public void UpdateInterpolation( float dt )
	{
		if ( !_hasInitialPosition )
		{
			DisplayPosition = CameraPosition;
			DisplayAngles = CameraAngles;
			_hasInitialPosition = true;
			return;
		}

		// Frame-rate independent exponential lerp for butter-smooth camera movement
		float factor = 1f - MathF.Exp( -22f * MathF.Max( dt, 0.001f ) );
		DisplayPosition = Vector3.Lerp( DisplayPosition, CameraPosition, factor );
		DisplayAngles = Angles.Lerp( DisplayAngles, CameraAngles, factor );
	}

	public CollaboratorState( string peerId, string personaName, ulong steamId, string colorHex = null )
	{
		PeerId = peerId;
		PersonaName = personaName;
		SteamId = steamId;

		if ( string.IsNullOrEmpty( colorHex ) )
		{
			Color = GenerateDeterministicColor( steamId != 0 ? steamId : (ulong)peerId.GetHashCode() );
			ColorHex = Color.Hex;
		}
		else
		{
			ColorHex = colorHex;
			Color = Color.Parse( colorHex ) ?? Color.Cyan;
		}
	}

	/// <summary>
	/// Generates a vibrant, easily distinguishable color from a seed (e.g. SteamId or PeerId).
	/// </summary>
	public static Color GenerateDeterministicColor( ulong seed )
	{
		// Preset vibrant designer palette for team collaboration
		Color[] palette = new[]
		{
			Color.Parse( "#3B82F6" ).Value, // Blue
			Color.Parse( "#10B981" ).Value, // Emerald
			Color.Parse( "#F59E0B" ).Value, // Amber
			Color.Parse( "#EC4899" ).Value, // Pink
			Color.Parse( "#8B5CF6" ).Value, // Violet
			Color.Parse( "#06B6D4" ).Value, // Cyan
			Color.Parse( "#EF4444" ).Value, // Red
			Color.Parse( "#14B8A6" ).Value, // Teal
			Color.Parse( "#F97316" ).Value, // Orange
			Color.Parse( "#A855F7" ).Value  // Purple
		};

		int index = (int)(seed % (ulong)palette.Length);
		return palette[index];
	}

	public PeerInfo ToPeerInfo()
	{
		return new PeerInfo
		{
			PeerId = PeerId,
			PersonaName = PersonaName,
			SteamId = SteamId,
			ColorHex = ColorHex,
			IsHost = IsHost,
			CameraPosition = CameraPosition,
			CameraAngles = CameraAngles,
			SelectedObjectIds = SelectedObjectIds.ToList(),
			LastSeenTimestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
		};
	}

	public void UpdateFromPeerInfo( PeerInfo info )
	{
		PersonaName = info.PersonaName ?? PersonaName;
		SteamId = info.SteamId;
		IsHost = info.IsHost;
		CameraPosition = info.CameraPosition;
		CameraAngles = info.CameraAngles;
		LastSeen = DateTime.UtcNow;

		if ( !string.IsNullOrEmpty( info.ColorHex ) && info.ColorHex != ColorHex )
		{
			ColorHex = info.ColorHex;
			Color = Color.Parse( info.ColorHex ) ?? Color;
		}

		SelectedObjectIds.Clear();
		if ( info.SelectedObjectIds != null )
		{
			foreach ( var id in info.SelectedObjectIds )
			{
				SelectedObjectIds.Add( id );
			}
		}
	}
}
