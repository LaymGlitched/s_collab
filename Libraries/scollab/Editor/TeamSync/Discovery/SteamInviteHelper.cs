using System.Diagnostics;

namespace Editor.TeamSync;

/// <summary>
/// Helper for Steam friend invitations, Steam overlay integration,
/// and instant one-click invite generation.
/// </summary>
public static class SteamInviteHelper
{
	/// <summary>
	/// Generates a rich invite message, copies it to the OS clipboard,
	/// and triggers the Steam Friends / Chat overlay window so the user
	/// can immediately paste and invite any Steam friend.
	/// </summary>
	public static void InviteViaSteam( string hostName, string projectName, string roomCode, string directEndpoint )
	{
		// 1. Format and copy invite to clipboard
		string message = SessionCode.CreateInviteMessage( hostName, projectName, roomCode, directEndpoint );
		EditorUtility.Clipboard.Copy( message );

		Log.Info( $"[TeamSync] Invite copied to clipboard for '{projectName}' (Room Code: {roomCode})!" );

		// 2. Open Steam Friends window / overlay
		OpenSteamFriendsOverlay();
	}

	/// <summary>
	/// Opens the Steam Friends list / overlay using standard Steam client URL handler.
	/// </summary>
	public static void OpenSteamFriendsOverlay()
	{
		try
		{
			var psi = new ProcessStartInfo
			{
				FileName = "steam://open/friends",
				UseShellExecute = true
			};
			Process.Start( psi );
		}
		catch ( Exception ex )
		{
			Log.Warning( $"[TeamSync] Could not open Steam client window: {ex.Message}. The invite is already in your clipboard!" );
		}
	}
}
