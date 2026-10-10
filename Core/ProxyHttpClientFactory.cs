// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Net;

namespace SteamFriendMonitor.Core;

// 创建走代理的 HttpClient：认证请求和 WebSocket CM 连接都经它出去
// HTTP 代理走 CONNECT 隧道；SOCKS5 由 .NET (SocketsHttpHandler) 原生支持
public static class ProxyHttpClientFactory {
	public static HttpClient Create(AppConfig config) {
		var handler = new SocketsHttpHandler {
			UseCookies = false,
			AllowAutoRedirect = true,
			AutomaticDecompression = System.Net.DecompressionMethods.All
		};

		if (config.ProxyEnabled) {
			var proxy = new WebProxy(new Uri($"{config.ProxyScheme}://{config.ProxyHost}:{config.ProxyPort}"));

			if (!string.IsNullOrWhiteSpace(config.ProxyUsername)) {
				proxy.Credentials = new NetworkCredential(config.ProxyUsername, config.ProxyPassword);
			}

			handler.Proxy = proxy;
			handler.UseProxy = true;
		}

		var client = new HttpClient(handler) {
			Timeout = TimeSpan.FromSeconds(60)
		};
		client.DefaultRequestHeaders.UserAgent.ParseAdd("SteamFriendMonitor/1.1");

		return client;
	}
}
