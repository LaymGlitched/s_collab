namespace Editor.TeamSync;

/// <summary>
/// Manages both 3D Gizmo viewport drawing (frustum, nameplates) and lightweight
/// SceneModel representations in the SceneWorld for all remote collaborators.
/// </summary>
public static class PresenceOverlay
{
	private static readonly Dictionary<string, SceneModel> _markerModels = new();

	public static void CleanupAllMarkers()
	{
		foreach ( var kvp in _markerModels )
		{
			if ( kvp.Value != null && kvp.Value.IsValid() )
			{
				kvp.Value.Delete();
			}
		}
		_markerModels.Clear();
	}

	public static void RemoveMarker( string peerId )
	{
		if ( _markerModels.TryGetValue( peerId, out var model ) )
		{
			if ( model != null && model.IsValid() )
			{
				model.Delete();
			}
			_markerModels.Remove( peerId );
		}
	}

	[EditorEvent.Frame]
	public static void FrameUpdate()
	{
		try
		{
			var manager = TeamSyncManager.Instance;
			if ( manager == null || !manager.IsSessionActive || Game.IsPlaying )
			{
				CleanupAllMarkers();
				return;
			}

			var session = SceneEditorSession.Active;
			if ( session == null || session.Scene == null || session.Scene.SceneWorld == null )
			{
				CleanupAllMarkers();
				return;
			}

			var activePeerIds = new HashSet<string>();
			float dt = RealTime.Delta;

			// Update or spawn SceneModel visual markers for each remote collaborator
			foreach ( var peer in manager.Collaborators.Values )
			{
				if ( peer.PeerId == manager.LocalPeerId ) continue; // Don't draw self

				peer.UpdateInterpolation( dt );
				activePeerIds.Add( peer.PeerId );
				UpdatePeerMarker( session.Scene.SceneWorld, peer );
			}

			// Prune markers for peers that left
			var toRemove = _markerModels.Keys.Where( k => !activePeerIds.Contains( k ) ).ToList();
			foreach ( var id in toRemove )
			{
				RemoveMarker( id );
			}

			// Draw Gizmos in 3D Viewport
			DrawPresenceGizmos( manager );
		}
		catch
		{
			// Prevent any frame hook exceptions from crashing the event system
		}
	}

	private static void UpdatePeerMarker( SceneWorld sceneWorld, CollaboratorState peer )
	{
		try
		{
			var renderPos = peer.DisplayPosition;
			var renderRot = peer.DisplayAngles.ToRotation();

			if ( !_markerModels.TryGetValue( peer.PeerId, out var model ) || model == null || !model.IsValid() )
			{
				var modelAsset = Model.Load( "models/editor/camera.vmdl" );
				if ( modelAsset == null || !modelAsset.IsValid() )
				{
					modelAsset = Model.Cube;
				}

				var transform = new Transform( renderPos, renderRot, 2.75f );
				
				model = new SceneModel( sceneWorld, modelAsset, transform );
				model.ColorTint = peer.Color;

				_markerModels[peer.PeerId] = model;
			}

			// Sync transform & color
			model.Transform = new Transform( renderPos, renderRot, 2.75f );
			if ( model.ColorTint != peer.Color )
			{
				model.ColorTint = peer.Color;
			}
		}
		catch
		{
			// Safe fallback
		}
	}

	private static void DrawPresenceGizmos( TeamSyncManager manager )
	{
		try
		{
			if ( Gizmo.Camera == null ) return;

			using ( Gizmo.Scope( "TeamSync_Presence" ) )
			{
				foreach ( var peer in manager.Collaborators.Values )
				{
					if ( peer.PeerId == manager.LocalPeerId ) continue;

					DrawCollaboratorPresence( peer );
				}
			}
		}
		catch
		{
			// Safely ignore when Gizmo rendering context is not active on this tick
		}
	}

	private static void DrawCollaboratorPresence( CollaboratorState peer )
	{
		var pos = peer.DisplayPosition;
		var rot = peer.DisplayAngles.ToRotation();
		var color = peer.Color;

		Gizmo.Draw.Color = color;
		Gizmo.Draw.LineThickness = 4.5f;

		// 1. Draw Camera Body (clear, prominent, highly visible frustum)
		float distance = 130f;
		float fovRad = MathX.DegreeToRadian( peer.CameraFov > 0 ? peer.CameraFov : 80f );
		float aspect = 16f / 9f;
		float halfHeight = MathF.Tan( fovRad / 2f ) * distance;
		float halfWidth = halfHeight * aspect;

		Vector3 forward = rot.Forward;
		Vector3 right = rot.Right;
		Vector3 up = rot.Up;

		Vector3 centerFar = pos + forward * distance;
		Vector3 c0 = centerFar + up * halfHeight - right * halfWidth; // Top-Left
		Vector3 c1 = centerFar + up * halfHeight + right * halfWidth; // Top-Right
		Vector3 c2 = centerFar - up * halfHeight + right * halfWidth; // Bottom-Right
		Vector3 c3 = centerFar - up * halfHeight - right * halfWidth; // Bottom-Left

		// Lines from camera origin to 4 corners
		Gizmo.Draw.Line( pos, c0 );
		Gizmo.Draw.Line( pos, c1 );
		Gizmo.Draw.Line( pos, c2 );
		Gizmo.Draw.Line( pos, c3 );

		// Far plane rectangle
		Gizmo.Draw.Line( c0, c1 );
		Gizmo.Draw.Line( c1, c2 );
		Gizmo.Draw.Line( c2, c3 );
		Gizmo.Draw.Line( c3, c0 );

		// Orientation line at the center
		Gizmo.Draw.Line( pos, pos + forward * 45f );

		// 2. Draw 3D Billboard Nameplate above the camera (always facing the local viewer)
		Vector3 textPos = pos + Vector3.Up * 48f;
		Vector3 toCam = Gizmo.Camera != null ? (Gizmo.Camera.Position - textPos) : -forward;
		if ( toCam.LengthSquared < 0.01f ) toCam = -forward;
		var billboardRot = Rotation.LookAt( toCam.Normal, Vector3.Up );

		float dist = toCam.Length;
		float textScale = Math.Clamp( dist / 150f, 1.2f, 4.0f );
		var textTransform = new Transform( textPos, billboardRot, textScale );

		Gizmo.Draw.Color = color;
		string label = string.IsNullOrEmpty( peer.PersonaName ) ? $"Peer {peer.PeerId.Substring( 0, 4 )}" : peer.PersonaName;
		if ( peer.IsHost ) label += " ★ (Host)";

		Gizmo.Draw.WorldText( label, textTransform, "Poppins", 32f, TextFlag.Center );
	}
}
