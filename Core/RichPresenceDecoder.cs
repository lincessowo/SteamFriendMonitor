using SteamKit2;

namespace SteamFriendMonitor.Core;

// 解码 persona 的 GameDataBlob（二进制 KeyValue），提取游戏富状态
// （如 CS2 的地图 / 比分 DUST2 6:2），与游戏名分开存储，避免污染统计
public static class RichPresenceDecoder {
	public static string? Decode(byte[]? blob) {
		if (blob is not { Length: > 4 }) {
			return null;
		}

		try {
			var root = new KeyValue();
			using var stream = new MemoryStream(blob);

			// 注意：SteamKit 在顶层遇到 End 标记会返回 false，但子节点已正确解析，
			// 所以这里不能把返回值当失败依据，只看后续能否取出有效键值对
			root.TryReadAsBinary(stream);

			// 只保留有信息量的键值对：跳过 steam_ 系统键和 # 开头的本地化令牌
			string joined = string.Join(" | ",
				EnumerateLeaves(root, string.Empty)
					.Where(p => p.Key.Length > 0
							 && !p.Key.StartsWith("steam_", StringComparison.OrdinalIgnoreCase)
							 && !p.Value.StartsWith('#'))
					.Select(p => $"{p.Key}={p.Value}"));

			if (joined.Length == 0) {
				return null;
			}

			return joined.Length > 300 ? joined[..300] : joined;
		} catch (Exception) {
			return null; // 解析失败不影响主流程
		}
	}

	static IEnumerable<(string Key, string Value)> EnumerateLeaves(KeyValue node, string prefix) {
		if (node.Children.Count == 0) {
			yield return (prefix, node.Value ?? string.Empty);
			yield break;
		}

		foreach (KeyValue child in node.Children) {
			string childName = child.Name ?? string.Empty;
			string key = prefix.Length == 0 ? childName : $"{prefix}.{childName}";

			foreach ((string, string) pair in EnumerateLeaves(child, key)) {
				yield return pair;
			}
		}
	}
}
