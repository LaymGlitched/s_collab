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

		// Update or spawn SceneModel visual markers for each remote collaborator
		foreach ( var peer in manager.Collaborators.Values )
		{
			if ( peer.PeerId == manager.LocalPeerId ) continue; // Don't draw self

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

	private static void UpdatePeerMarker( SceneWorld sceneWorld, CollaboratorState peer )
	{
		if ( !_markerModels.TryGetValue( peer.PeerId, out var model ) || model == null || !model.IsValid() )
		{
			var cameraModel = Model.Load( "models/editor/camera.vmdl" );
			var transform = new Transform( peer.CameraPosition, peer.CameraAngles.ToRotation() );
			
			model = new SceneModel( sceneWorld, cameraModel, transform );
			model.ColorTint = peer.Color;

			_markerModels[peer.PeerId] = model;
		}

		// Sync transform & color
		model.Transform = new Transform( peer.CameraPosition, peer.CameraAngles.ToRotation() );
		if ( model.ColorTint != peer.Color )
		{
			model.ColorTint = peer.Color;
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
		var pos = peer.CameraPosition;
		var rot = peer.CameraAngles.ToRotation();
		var color = peer.Color;

		Gizmo.Draw.Color = color;
		Gizmo.Draw.LineThickness = 2f;

		// 1. Draw Camera Body (compact pyramid/frustum)
		float distance = 40f;
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

		// Small orientation line at the center
		Gizmo.Draw.Line( pos, pos + forward * 15f );

		// 2. Draw 3D Floating Nameplate above the camera
		Vector3 textPos = pos + Vector3.Up * 24f;
		var textTransform = new Transform( textPos, Rotation.LookAt( -forward, Vector3.Up ), 1.0f );

		Gizmo.Draw.Color = color;
		string label = string.IsNullOrEmpty( peer.PersonaName ) ? $"Peer {peer.PeerId.Substring( 0, 4 )}" : peer.PersonaName;
		if ( peer.IsHost ) label += " (Host)";

		Gizmo.Draw.WorldText( label, textTransform, "Poppins", 24f, TextFlag.Center );
	}
}
