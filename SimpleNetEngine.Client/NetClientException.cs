using System;
using Google.Protobuf;

namespace SimpleNetEngine.Client
{
    public class NetClientException : Exception
    {
        public NetClientException(ushort errorCode, IMessage request)
            : base($"An error occurred - [errorCode:{errorCode},req msgId:{request.Descriptor.Name}]")
        {
            ErrorCode = errorCode;
            Request = request;
        }

        public NetClientException(ushort errorCode, string message, IMessage request)
            : base(message)
        {
            ErrorCode = errorCode;
            Request = request;
        }

        public NetClientException(ushort errorCode, string message, Exception innerException, IMessage request)
            : base(message, innerException)
        {
            ErrorCode = errorCode;
            Request = request;
        }

        public IMessage Request { get; private set; }
        public ushort ErrorCode { get; private set; }
    }
}