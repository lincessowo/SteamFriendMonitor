// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

namespace SteamFriendMonitor;

internal static class Program {
	[STAThread]
	static int Main() {
		ApplicationConfiguration.Initialize();
		Application.Run(new UI.MainForm());
		return 0;
	}
}
