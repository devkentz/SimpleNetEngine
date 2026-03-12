using System.IO.Hashing;

namespace SimpleNetEngine.Protocol.Utils
{
    /// <summary>
    /// Twitter Snowflake 알고리즘 기반의 분산 시스템용 고유 ID 생성기입니다.
    /// 생성된 ID는 시간 기반으로 정렬되며 노드 ID로 구분됩니다.
    /// </summary>
    public class UniqueIdGenerator
    {
        // 비트 할당 상수
        private const int NodeIdBits = 12;
        private const int SequenceBits = 10;
        
        // 시프트 상수
        private const int NodeIdShift = SequenceBits;
        private const int TimestampShift = NodeIdBits + SequenceBits;
        
        // 최대값 상수
        private const long MaxNodeId = (1L << NodeIdBits) - 1;  // 4095
        private const long MaxSequence = (1L << SequenceBits) - 1;  // 1023
        
        // 기준 시간 (2020년 1월 1일 UTC)
        private static readonly DateTimeOffset EpochStart = new DateTimeOffset(2020, 1, 1, 0, 0, 0, TimeSpan.Zero);
        private static readonly long Epoch = EpochStart.ToUnixTimeMilliseconds();

        private readonly long _nodeId;
        private long _lastTimestamp = -1L;
        private long _sequence;
        private readonly Lock _lock = new();

        /// <summary>
        /// UniqueIdGenerator의 새 인스턴스를 초기화합니다.
        /// </summary>
        /// <param name="nodeId">현재 노드를 식별하는 Guid</param>
        public UniqueIdGenerator(Guid nodeId)
        {
            // Guid를 12비트 노드 ID로 해시화
            _nodeId = ComputeNodeIdFromGuid(nodeId);
        }

        /// <summary>
        /// UniqueIdGenerator의 새 인스턴스를 초기화합니다. (기존 호환성)
        /// </summary>
        /// <param name="nodeId">현재 노드를 식별하는 ID (0-4095 사이)</param>
        /// <exception cref="ArgumentOutOfRangeException">nodeId가 유효한 범위를 벗어날 경우</exception>
        public UniqueIdGenerator(int nodeId)
        {
            if (nodeId < 0 || nodeId > MaxNodeId)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(nodeId), 
                    $"Node ID must be between 0 and {MaxNodeId}."
                );
            }

            _nodeId = nodeId;
        }

        /// <summary>
        /// Guid에서 12비트 노드 ID를 계산합니다.
        /// XxHash64를 사용하여 최고 품질의 해시 분포를 보장합니다.
        /// </summary>
        /// <param name="guid">노드를 식별하는 Guid</param>
        /// <returns>0-4095 사이의 노드 ID</returns>
        private static long ComputeNodeIdFromGuid(Guid guid)
        {
            // Guid의 바이트를 가져와서 XxHash64 계산
            var guidBytes = guid.ToByteArray();
            
            // XxHash64로 64비트 해시값 생성
            var hashBytes = XxHash64.Hash(guidBytes);
            var hash = BitConverter.ToUInt64(hashBytes, 0);
            
            // 64비트 해시를 12비트로 축약 (상위 비트와 하위 비트 조합으로 품질 향상)
            var folded = hash ^ (hash >> 32) ^ (hash >> 16) ^ (hash >> 12);
            
            // 12비트 마스크 적용하여 0-4095 범위로 제한
            return (long)(folded & MaxNodeId);
        }

        /// <summary>
        /// 전역적으로 고유한 ID를 생성합니다.
        /// </summary>
        /// <returns>64비트 고유 ID</returns>
        /// <exception cref="InvalidOperationException">시스템 시계가 이전 타임스탬프보다 이전으로 조정된 경우</exception>
        public long NextId()
        {
            lock (_lock)
            {
                var timestamp = GetCurrentTimestamp();
                EnsureTimeIsMovingForward(timestamp);

                UpdateSequenceCounter(ref timestamp);
                _lastTimestamp = timestamp;

                return GenerateId(timestamp);
            }
        }

        /// <summary>
        /// 비동기 환경에서 사용 가능한 고유 ID를 생성합니다.
        /// </summary>
        /// <returns>64비트 고유 ID</returns>
        public long NextIdNonBlocking()
        {
            long timestamp, sequence, lastTimestamp;
            long id;

            do
            {
                timestamp = GetCurrentTimestamp();
                
                // 스냅샷 생성
                lastTimestamp = Interlocked.Read(ref _lastTimestamp);
                
                if (timestamp < lastTimestamp)
                {
                    throw new InvalidOperationException("Invalid system clock: clock moved backwards.");
                }

                sequence = Interlocked.Read(ref _sequence);
                
                if (timestamp == lastTimestamp)
                {
                    sequence = (sequence + 1) & MaxSequence;
                    
                    // 시퀀스가 오버플로우된 경우 대기하지 않고 재시도
                    if (sequence == 0)
                    {
                        continue;
                    }
                    
                    // 시퀀스 업데이트 시도
                    if (Interlocked.CompareExchange(ref _sequence, sequence, sequence - 1) != sequence - 1)
                    {
                        continue; // 다른 스레드가 변경했으면 재시도
                    }
                }
                else
                {
                    // 새로운 밀리초로 넘어갔을 때 시퀀스를 0으로 리셋
                    if (Interlocked.CompareExchange(ref _sequence, 0, sequence) != sequence)
                    {
                        continue; // 다른 스레드가 변경했으면 재시도
                    }
                    
                    sequence = 0;
                    
                    // 타임스탬프 업데이트 시도
                    if (Interlocked.CompareExchange(ref _lastTimestamp, timestamp, lastTimestamp) != lastTimestamp)
                    {
                        continue; // 다른 스레드가 변경했으면 재시도
                    }
                }
                
                id = ConstructId(timestamp, sequence);
                break;
                
            } while (true);

            return id;
        }

        /// <summary>
        /// 시계가 올바른 방향으로 진행 중인지 확인합니다.
        /// </summary>
        private void EnsureTimeIsMovingForward(long currentTimestamp)
        {
            if (currentTimestamp < _lastTimestamp)
            {
                var drift = _lastTimestamp - currentTimestamp;
                throw new InvalidOperationException(
                    $"Clock moved backwards. Refusing to generate ID for {drift} milliseconds."
                );
            }
        }

        /// <summary>
        /// 시퀀스 카운터를 업데이트하고 필요시 대기합니다.
        /// </summary>
        private void UpdateSequenceCounter(ref long timestamp)
        {
            if (timestamp == _lastTimestamp)
            {
                // 같은 밀리초 내에서는 시퀀스를 증가
                _sequence = (_sequence + 1) & MaxSequence;
                
                // 시퀀스가 한 바퀴 돌았으면 다음 밀리초까지 대기
                if (_sequence == 0)
                {
                    timestamp = WaitForNextMillisecond(_lastTimestamp);
                }
            }
            else
            {
                // 새로운 밀리초에서는 시퀀스 리셋
                _sequence = 0;
            }
        }

        /// <summary>
        /// 최종 ID를 구성합니다.
        /// </summary>
        private long GenerateId(long timestamp)
        {
            return ConstructId(timestamp, _sequence);
        }

        /// <summary>
        /// 주어진 타임스탬프와 시퀀스로 ID를 구성합니다.
        /// </summary>
        private long ConstructId(long timestamp, long sequence)
        {
            return ((timestamp - Epoch) << TimestampShift) |
                   (_nodeId << NodeIdShift) |
                   sequence;
        }

        /// <summary>
        /// 현재 타임스탬프를 밀리초 단위로 가져옵니다.
        /// </summary>
        private static long GetCurrentTimestamp()
        {
            return DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        }

        /// <summary>
        /// 지정된 타임스탬프보다 큰 타임스탬프를 기다립니다.
        /// </summary>
        private static long WaitForNextMillisecond(long lastTimestamp)
        {
            long timestamp;
            do
            {
                timestamp = GetCurrentTimestamp();
            } while (timestamp <= lastTimestamp);

            return timestamp;
        }

        /// <summary>
        /// 주어진 ID에서 타임스탬프 부분을 추출합니다.
        /// </summary>
        public static DateTimeOffset ExtractTimestamp(long id)
        {
            var milliseconds = (id >> TimestampShift) + Epoch;
            return DateTimeOffset.FromUnixTimeMilliseconds(milliseconds);
        }

        /// <summary>
        /// 주어진 ID에서 노드 ID 부분을 추출합니다.
        /// </summary>
        public static int ExtractNodeId(long id)
        {
            return (int)((id >> NodeIdShift) & MaxNodeId);
        }

        /// <summary>
        /// 주어진 ID에서 시퀀스 부분을 추출합니다.
        /// </summary>
        public static int ExtractSequence(long id)
        {
            return (int)(id & MaxSequence);
        }
    }
}