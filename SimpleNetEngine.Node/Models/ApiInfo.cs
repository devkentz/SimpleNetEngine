using Internal.Protocol;

namespace SimpleNetEngine.Node.Models;

public class ApiInfo
{
    public string ApiName { get; set; } = string.Empty;
    public EServerType ServerType { get; set; }
}