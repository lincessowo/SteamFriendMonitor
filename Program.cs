namespace SteamFriendMonitor;

internal static class Program {
	[STAThread]
	static int Main() {
		ApplicationConfiguration.Initialize();
		Application.Run(new UI.MainForm());
		return 0;
	}
}
