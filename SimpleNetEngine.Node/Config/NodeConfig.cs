using Internal.Protocol;
using SimpleNetEngine.Protocol.Utils;

namespace SimpleNetEngine.Node.Config
{
    public class NodeConfig
    {
        public NodeConfig()
        {
            // 생성자에서 초기 NodeId 계산
            _nodeId = HashHelper.XxHash64(_nodeGuid.ToByteArray());
        }
        
        public string RedisConnectionString { get; set; } = string.Empty;
        public string ServerRegistryKey { get; set; } = string.Empty;
        public EServerType ServerType { get; set; }

        /// <summary>
        /// 서비스 이름 (Api 타입 노드의 그룹핑 키).
        /// Gateway/Game은 ServerType으로 자동 결정되므로 설정 불필요.
        /// Api 타입은 반드시 설정해야 올바르게 그룹핑됨.
        /// </summary>
        public string Name { get; set; } = string.Empty;

        public string Host { get; set; } = string.Empty;
        public int Port { get; set; }
        
        /// <summary>
        /// 서버의 추가 메타데이터 정보 (Gateway 연결 포트 등)
        /// </summary>
        public Dictionary<string, string> Metadata { get; set; } = new();

        // Timeout & Interval Settings (Default values)
        public int HeartBeatIntervalSeconds { get; set; } = 5;
        public int HeartBeatTtlSeconds { get; set; } = 15;
        public int IdentityExchangeDelayMs { get; set; } = 50;
        public int HandShakeTimeoutMs { get; set; } = 5000;
        public int MaxHandShakeRetries { get; set; } = 3;
        public int RequestTimeoutMs { get; set; } = 5000;

        public Guid NodeGuid
        {
            get => _nodeGuid;
            set
            {
                _nodeGuid = value;
                // 명시적 NodeId가 해시값과 동일하거나 세팅되지 않았을 때 자동 갱신
                if (_nodeId == 0 || _nodeId == HashHelper.XxHash64(Guid.Empty.ToByteArray()))
                    _nodeId = HashHelper.XxHash64(value.ToByteArray());
            }
        }

        private Guid _nodeGuid;
        
        public long NodeId 
        { 
            get => _nodeId; 
            set => _nodeId = value; 
        }
        private long _nodeId = 0;
    }
}