using Editor;

namespace Editor.TeamSync;

/// <summary>
/// Top editor menu shortcuts for s&collab.
/// </summary>
public static class MyEditorMenu
{
	[Menu( "Editor", "s&&collab/Team Sync Window", "groups" )]
	public static void OpenTeamSync()
	{
		TeamSyncDock.OpenWindow();
	}

	[Menu( "Editor", "s&&collab/Chat && Activity", "chat" )]
	public static void OpenChat()
	{
		TeamSyncChatDock.OpenWindow();
	}

	[Menu( "Editor", "s&&collab/Run Diagnostics && Tests", "checklist" )]
	public static void RunTests()
	{
		TeamSyncTests.RunAllTestsCmd();
	}
}
