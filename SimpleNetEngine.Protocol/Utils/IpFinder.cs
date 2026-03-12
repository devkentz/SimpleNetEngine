using System.Net;
using System.Net.Sockets;

namespace SimpleNetEngine.Protocol.Utils
{
    /// <summary>
    /// IP 주소 및 포트 관련 유틸리티 기능을 제공합니다.
    /// </summary>
    public static class IpFinder
    {
        // 공용 IP 조회에 사용할 서비스 목록
        private static readonly IReadOnlyList<string> PublicIpServiceUrls = new List<string>
        {
            "https://checkip.amazonaws.com/",
            "https://ipv4.icanhazip.com/",
            "https://myexternalip.com/raw",
            "https://ipecho.net/plain"
        };

        // HTTP 요청 타임아웃 시간
        private static readonly TimeSpan HttpTimeout = TimeSpan.FromSeconds(5);

        /// <summary>
        /// 로컬 IP 주소를 찾습니다.
        /// </summary>
        /// <returns>로컬 IP 주소 문자열</returns>
        public static string FindLocalIp()
        {
            try
            {
                return GetLocalIpByExternalConnection();
            }
            catch (Exception)
            {
                // 로그 추가 고려
                // Logger.LogWarning($"Failed to get local IP by external connection: {ex.Message}");
                return GetLocalIpByHostName();
            }
        }

        /// <summary>
        /// 외부에서 확인할 수 있는 공용 IP 주소를 찾습니다.
        /// </summary>
        /// <returns>공용 IP 주소 문자열 또는 실패 시 로컬 IP</returns>
        public static string FindPublicIp()
        {
            foreach (var url in PublicIpServiceUrls)
            {
                try
                {
                    var ipString = GetPublicIpFromService(url);
                    if (IsValidIpv4(ipString))
                    {
                        return ipString.Trim();
                    }
                }
                catch (Exception)
                {
                    // 이 서비스 실패 시 다음 서비스로 계속 진행
                    continue;
                }
            }

            // 모든 서비스 실패 시 로컬 IP 반환
            return FindLocalIp();
        }

        /// <summary>
        /// 공용 IP 주소를 비동기적으로 찾습니다.
        /// </summary>
        /// <param name="cancellationToken">취소 토큰</param>
        /// <returns>공용 IP 주소 문자열 또는 실패 시 로컬 IP</returns>
        public static async Task<string> FindPublicIpAsync(CancellationToken cancellationToken = default)
        {
            foreach (var url in PublicIpServiceUrls)
            {
                try
                {
                    var ipString = await GetPublicIpFromServiceAsync(url, cancellationToken);
                    if (IsValidIpv4(ipString))
                    {
                        return ipString.Trim();
                    }
                }
                catch (Exception)
                {
                    // 이 서비스 실패 시 다음 서비스로 계속 진행
                    continue;
                }
            }

            // 모든 서비스 실패 시 로컬 IP 반환
            return FindLocalIp();
        }

        /// <summary>
        /// 사용 가능한 무작위 TCP 포트를 찾습니다.
        /// </summary>
        /// <returns>사용 가능한 TCP 포트 번호</returns>
        public static int FindFreePort()
        {
            try
            {
                using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                socket.Bind(new IPEndPoint(IPAddress.Any, 0));
                socket.Listen(1);

                if (socket.LocalEndPoint is IPEndPoint ipEndPoint)
                {
                    return ipEndPoint.Port;
                }

                throw new InvalidOperationException("Failed to get local endpoint from socket.");
            }
            catch (SocketException ex)
            {
                throw new InvalidOperationException($"Failed to find free port: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// 외부 연결을 통해 로컬 IP를 확인합니다.
        /// </summary>
        private static string GetLocalIpByExternalConnection()
        {
            using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);

            // 구글 DNS 서버에 연결 시도 (더 신뢰성 있음)
            socket.Connect(new IPEndPoint(IPAddress.Parse("8.8.8.8"), 53));

            if (socket.LocalEndPoint is IPEndPoint ipEndPoint)
            {
                return ipEndPoint.Address.ToString();
            }

            throw new InvalidOperationException("Failed to get local endpoint.");
        }

        /// <summary>
        /// 호스트 이름을 통해 로컬 IP를 확인합니다.
        /// </summary>
        private static string GetLocalIpByHostName()
        {
            string hostName = Dns.GetHostName();
            IPAddress[] addresses = Dns.GetHostAddresses(hostName);

            // IPv4 주소 중 첫 번째 찾기
            foreach (var address in addresses)
            {
                if (address.AddressFamily == AddressFamily.InterNetwork)
                {
                    return address.ToString();
                }
            }

            // IPv4 주소가 없으면 첫 번째 주소 반환
            return addresses.Length > 0 ? addresses[0].ToString() : "127.0.0.1";
        }

        /// <summary>
        /// 지정된 서비스에서 공용 IP를 가져옵니다.
        /// </summary>
        private static string GetPublicIpFromService(string url)
        {
            using var client = new HttpClient();
            client.Timeout = HttpTimeout;

            var response = client.GetAsync(url).Result;
            response.EnsureSuccessStatusCode();

            var content = response.Content.ReadAsStringAsync().Result;
            return content.Trim();
        }

        /// <summary>
        /// 지정된 서비스에서 공용 IP를 비동기적으로 가져옵니다.
        /// </summary>
        private static async Task<string> GetPublicIpFromServiceAsync(string url, CancellationToken cancellationToken)
        {
            using var client = new HttpClient();
            client.Timeout = HttpTimeout;

            var response = await client.GetAsync(url, cancellationToken);
            response.EnsureSuccessStatusCode();

            var content = await response.Content.ReadAsStringAsync(cancellationToken);
            return content.Trim();
        }

        /// <summary>
        /// 문자열이 유효한 IPv4 주소인지 확인합니다.
        /// </summary>
        private static bool IsValidIpv4(string ipString)
        {
            if (string.IsNullOrWhiteSpace(ipString))
                return false;

            return IPAddress.TryParse(ipString.Trim(), out var address) &&
                   address.AddressFamily == AddressFamily.InterNetwork;
        }
    }
}