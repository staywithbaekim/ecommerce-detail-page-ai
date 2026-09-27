namespace Desktop.Contracts.Bridge;

/// <summary>Only locally owned messages may leave the adapter; never serialize caught exceptions.</summary>
public static class BridgeWireErrors
{
    public static BridgeWireErrorV1 Create(string code) => code switch
    {
        "BRIDGE_PROTOCOL_MISMATCH" => new(code, false, "session", "升级配套组件。"),
        "BRIDGE_PAIRING_REJECTED" => new(code, false, "session", "请重新配对 Photoshop。"),
        "BRIDGE_TOKEN_INVALID" => new(code, false, "session", "请重新连接 Photoshop。"),
        "BRIDGE_PORT_UNAVAILABLE" => new(code, false, "host", "请关闭占用连接端口的程序后重试。"),
        "BRIDGE_REQUEST_TOO_LARGE" => new(code, false, "request", "请求超过当前协议限制。"),
        "BRIDGE_PLAN_INVALID" => new(code, false, "request", "请返回生成前检查并重新准备计划。"),
        "BRIDGE_RESOURCE_BINDING_INVALID" => new(code, false, "request", "请重新校验当前页面的资源副本。"),
        "BRIDGE_CANCEL_PENDING" => new(code, false, "execution", "当前页正在取消，等待安全释放确认。"),
        "BRIDGE_EXECUTION_NOT_FOUND" => new(code, false, "execution", "请检查页面恢复记录，不要重复提交。"),
        "BRIDGE_DISCONNECTED" => new(code, true, "session", "连接已断开，请查询执行状态后继续。"),
        "BRIDGE_HOST_BLOCKED" => new(code, false, "host", "请处理 Photoshop 未保存文档或弹窗后重试。"),
        "BRIDGE_EXECUTION_UNKNOWN" => new(code, false, "execution", "执行结果未知，请查询或人工确认。"),
        "BRIDGE_BUSY" => new(code, true, "host", "请等待当前 Photoshop 页面到达安全边界。"),
        _ => throw new ArgumentOutOfRangeException(nameof(code))
    };

    public static bool IsKnown(string code) => code is
        "BRIDGE_PROTOCOL_MISMATCH" or "BRIDGE_PAIRING_REJECTED" or "BRIDGE_TOKEN_INVALID"
        or "BRIDGE_PORT_UNAVAILABLE" or "BRIDGE_REQUEST_TOO_LARGE" or "BRIDGE_PLAN_INVALID"
        or "BRIDGE_RESOURCE_BINDING_INVALID" or "BRIDGE_CANCEL_PENDING" or "BRIDGE_EXECUTION_NOT_FOUND"
        or "BRIDGE_DISCONNECTED" or "BRIDGE_HOST_BLOCKED" or "BRIDGE_EXECUTION_UNKNOWN" or "BRIDGE_BUSY";
}
