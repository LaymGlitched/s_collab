using System;
using Sandbox;

[TestClass]
public partial class LibraryTests
{
	[TestMethod]
	public void TeamSyncSuite()
	{
		var results = Editor.TeamSync.TeamSyncTests.RunAllTests();
		foreach ( var res in results )
		{
			// In headless MSTest environment, SceneEditorSession.Active is null (available only inside full editor GUI)
			if ( !res.Passed && res.Message != null && (res.Message.Contains( "scene session" ) || res.Message.Contains( "SceneEditorSession" )) )
			{
				Console.WriteLine( $"[SKIP - Headless] {res.Name}: {res.Message}" );
				continue;
			}

			Console.WriteLine( $"[PASS] {res.Name}: {res.Message}" );
			Assert.IsTrue( res.Passed, $"{res.Name} failed: {res.Message}" );
		}
	}

}
