using SteamKit2;

namespace SteamFriendMonitor.Core;

public static class StatusText {
	// 把 Steam 好友状态翻译成中文描述；正在游戏时优先显示游戏名
	public static string Describe(EPersonaState state, string? gameName) {
		if (!string.IsNullOrEmpty(gameName)) {
			return state is EPersonaState.Offline or EPersonaState.Invisible
				? $"离线（游戏中：{gameName}）"
				: $"游戏中（{gameName}）";
		}

		return state switch {
			EPersonaState.Offline => "离线",
			EPersonaState.Online => "在线",
			EPersonaState.Busy => "忙碌",
			EPersonaState.Away => "离开",
			EPersonaState.Snooze => "打盹",
			EPersonaState.LookingToTrade => "寻找交易",
			EPersonaState.LookingToPlay => "寻找组队",
			EPersonaState.Invisible => "隐身",
			_ => state.ToString()
		};
	}
}
