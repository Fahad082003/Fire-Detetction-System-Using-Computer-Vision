namespace Sentinel.Api.Security;

public static class AuthPolicies
{
    /// <summary>Logged-in dashboard user (JWT) with role Admin or Operator.</summary>
    public const string Operator = "Operator";
    /// <summary>AI pipeline / IoT gateway presenting the X-Device-Key header.</summary>
    public const string Device = "Device";
    /// <summary>Either of the above (read endpoints the simulator needs, IoT simulation endpoints, hub connection).</summary>
    public const string OperatorOrDevice = "OperatorOrDevice";

    public const string DeviceScheme = "DeviceKey";
    public const string DeviceRole = "Device";
    public const string DeviceHeader = "X-Device-Key";
}
