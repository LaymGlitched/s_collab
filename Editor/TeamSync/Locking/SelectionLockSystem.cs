namespace Editor.TeamSync;

/// <summary>
/// Manages object-level selection locks to prevent edit conflicts.
/// Renders lock outlines/badges in the 3D viewport and enforces lock rules.
/// </summary>
public sealed class SelectionLockSystem
{
	private readonly TeamSyncManager _manager;

	// GameObjectId (string) -> PeerId (string)
	private readonly ConcurrentDictionary<string, string> _activeLocks = new();
	private readonly ConcurrentDictionary<string, DateTime> _lockTimestamps = new();

	public IReadOnlyDictionary<string, string> ActiveLocks => _activeLocks;

	public SelectionLockSystem( TeamSyncManager manager )
	{
		_manager = manager;
	}

	/// <summary>
	/// Checks if a GameObject is locked by another peer.
	/// </summary>
	public bool IsLockedByOther( string gameObjectId, out string ownerPeerId )
	{
		ownerPeerId = null;
		if ( string.IsNullOrEmpty( gameObjectId ) ) return false;

		if ( _activeLocks.TryGetValue( gameObjectId, out var peerId ) )
		{
			if ( peerId != _manager.LocalPeerId )
			{
				ownerPeerId = peerId;
				return true;
			}
		}
		return false;
	}

	/// <summary>
	/// Attempts to acquire a lock on a GameObject for a specific peer.
	/// Returns true if granted, false if already held by another peer.
	/// </summary>
	public bool TryAcquireLock( string gameObjectId, string peerId )
	{
		if ( string.IsNullOrEmpty( gameObjectId ) || string.IsNullOrEmpty( peerId ) ) return false;

		if ( _activeLocks.TryGetValue( gameObjectId, out var existingHolder ) && existingHolder != peerId )
		{
			return false; // Denied - held by someone else
		}

		_activeLocks[gameObjectId] = peerId;
		_lockTimestamps[gameObjectId] = DateTime.UtcNow;
		return true;
	}

	/// <summary>
	/// Attempts to release a lock on a GameObject for a specific peer.
	/// Returns true if released, false if not held by this peer.
	/// </summary>
	public bool TryReleaseLock( string gameObjectId, string peerId )
	{
		if ( string.IsNullOrEmpty( gameObjectId ) || string.IsNullOrEmpty( peerId ) ) return false;

		if ( _activeLocks.TryGetValue( gameObjectId, out var holder ) && holder == peerId )
		{
			_activeLocks.TryRemove( gameObjectId, out _ );
			_lockTimestamps.TryRemove( gameObjectId, out _ );
			return true;
		}
		return false;
	}

	/// <summary>
	/// Local client requesting to acquire or release a lock.
	/// </summary>
	public void RequestLock( string gameObjectId, bool acquire )
	{
		if ( !_manager.IsSessionActive || string.IsNullOrEmpty( gameObjectId ) ) return;

		var request = new LockRequestPayload
		{
			PeerId = _manager.LocalPeerId,
			GameObjectId = gameObjectId,
			Acquire = acquire
		};

		if ( _manager.IsHost )
		{
			HandleLockRequestOnHost( request );
		}
		else
		{
			_ = _manager.Transport?.SendAsync( TeamSyncEnvelope.Create( TeamSyncMessageType.LockRequest, _manager.LocalPeerId, request ) );
		}
	}

	/// <summary>
	/// Host handles lock requests and validates conflict rules.
	/// </summary>
	public void HandleLockRequestOnHost( LockRequestPayload request )
	{
		if ( request == null || string.IsNullOrEmpty( request.GameObjectId ) ) return;

		string goId = request.GameObjectId;
		string peerId = request.PeerId;

		if ( request.Acquire )
		{
			TryAcquireLock( goId, peerId );
		}
		else
		{
			TryReleaseLock( goId, peerId );
		}

		if ( _manager.IsHost )
		{
			BroadcastLockTable();
		}
	}

	/// <summary>
	/// Updates the local lock table from incoming network message.
	/// </summary>
	public void ApplyLockUpdate( Dictionary<string, string> locks )
	{
		_activeLocks.Clear();
		if ( locks != null )
		{
			foreach ( var kvp in locks )
			{
				_activeLocks[kvp.Key] = kvp.Value;
				_lockTimestamps[kvp.Key] = DateTime.UtcNow;
			}
		}

		// Enforce locks against local selection
		EnforceLocalSelectionLocks();
	}

	/// <summary>
	/// Releases all locks held by a disconnected peer.
	/// </summary>
	public void ReleaseLocksForPeer( string peerId )
	{
		if ( string.IsNullOrEmpty( peerId ) ) return;

		var toRemove = _activeLocks.Where( x => x.Value == peerId ).Select( x => x.Key ).ToList();
		foreach ( var id in toRemove )
		{
			_activeLocks.TryRemove( id, out _ );
			_lockTimestamps.TryRemove( id, out _ );
		}

		if ( _manager.IsHost && toRemove.Count > 0 )
		{
			BroadcastLockTable();
		}
	}

	/// <summary>
	/// Host periodically prunes expired locks.
	/// </summary>
	public void PruneExpiredLocks( TimeSpan timeout )
	{
		if ( !_manager.IsHost ) return;

		var now = DateTime.UtcNow;
		var expired = _lockTimestamps.Where( x => now - x.Value > timeout ).Select( x => x.Key ).ToList();

		if ( expired.Count > 0 )
		{
			foreach ( var id in expired )
			{
				_activeLocks.TryRemove( id, out _ );
				_lockTimestamps.TryRemove( id, out _ );
			}
			BroadcastLockTable();
		}
	}

	public void BroadcastLockTable()
	{
		if ( !_manager.IsHost || _manager.Transport == null ) return;

		var payload = new LockUpdatePayload
		{
			Locks = new Dictionary<string, string>( _activeLocks )
		};

		_ = _manager.Transport.BroadcastAsync( TeamSyncEnvelope.Create( TeamSyncMessageType.LockUpdate, _manager.LocalPeerId, payload ) );
	}

	/// <summary>
	/// Deselects any objects that local user has selected if locked by someone else.
	/// </summary>
	public void EnforceLocalSelectionLocks()
	{
		var session = SceneEditorSession.Active;
		if ( session == null ) return;

		var items = session.Selection.ToList();
		foreach ( var item in items )
		{
			GameObject go = null;
			if ( item is GameObject g && g.IsValid() )
			{
				go = g;
			}
			else if ( item is Component comp && comp.IsValid() && comp.GameObject != null && comp.GameObject.IsValid() )
			{
				go = comp.GameObject;
			}

			if ( go != null && IsLockedByOther( go.Id.ToString(), out _ ) )
			{
				session.Selection.Remove( item );
			}
		}
	}

	/// <summary>
	/// Draws visual lock highlights in the 3D viewport.
	/// </summary>
	public void DrawLockGizmos()
	{
		try
		{
			if ( Gizmo.Camera == null ) return;
			if ( !_manager.IsSessionActive || Game.IsPlaying ) return;

			var session = SceneEditorSession.Active;
			if ( session == null || session.Scene == null ) return;

			using ( Gizmo.Scope( "TeamSync_Locks" ) )
			{
				foreach ( var kvp in _activeLocks )
				{
					string goIdStr = kvp.Key;
					string holderPeerId = kvp.Value;

					if ( holderPeerId == _manager.LocalPeerId ) continue; // Local user knows their own selection

					var go = SceneApplicator.FindGameObject( session.Scene, goIdStr );
					if ( go == null || !go.IsValid() ) continue;

					if ( !_manager.Collaborators.TryGetValue( holderPeerId, out var peer ) ) continue;

					var color = peer.Color;
					Gizmo.Draw.Color = color;
					Gizmo.Draw.LineThickness = 3f;

					// Draw wire bounding box around locked object
					var bbox = go.GetBounds();
					if ( bbox.Size.Length > 0.1f )
					{
						Gizmo.Draw.LineBBox( bbox );
					}

					// Draw floating badge over the locked object
					Vector3 topPos = bbox.Center + Vector3.Up * (bbox.Size.z * 0.5f + 10f);
					var textTransform = new Transform( topPos, Rotation.Identity, 0.8f );
					string text = $"🔒 {peer.PersonaName}";
					Gizmo.Draw.WorldText( text, textTransform, "Poppins", 18f, TextFlag.Center );
				}
			}
		}
		catch
		{
			// Safely ignore when Gizmo rendering context is not active
		}
	}
}
