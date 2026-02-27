using System;
using System.Threading.Tasks;
using Nakama;
using UnityEngine;

/// <summary>
/// Nakama 初始化与登录入口类。
/// 挂载到场景中的 GameObject 上，在 Start 时自动进行设备匿名登录。
/// </summary>
public class Connector : MonoBehaviour
{
    [Header("服务器配置")]
    [SerializeField] private string scheme = "http";
    [SerializeField] private string host = "127.0.0.1";
    [SerializeField] private int port = 7350;
    [SerializeField] private string serverKey = "defaultkey";

    /// <summary>当前 Nakama 客户端实例</summary>
    public static IClient Client { get; private set; }

    /// <summary>当前会话（登录成功后赋值）</summary>
    public static ISession Session { get; private set; }

    /// <summary>实时通信 Socket（连接后赋值）</summary>
    public static ISocket Socket { get; private set; }

    /// <summary>登录完成事件</summary>
    public static event Action<ISession> OnLoginSuccess;

    /// <summary>登录失败事件</summary>
    public static event Action<Exception> OnLoginFailure;

    private async void Start()
    {
        // 初始化客户端
        Client = new Client(scheme, host, port, serverKey, UnityWebRequestAdapter.Instance)
        {
            Logger = new UnityLogger()
        };

        await LoginAsync();
    }

    /// <summary>
    /// 使用设备 ID 进行匿名登录（若账号不存在则自动注册）。
    /// </summary>
    private async Task LoginAsync()
    {
        // 使用设备唯一标识作为 deviceId
        string deviceId = GetOrCreateDeviceId();

        try
        {
            Debug.Log($"[Nakama] 正在登录，DeviceId: {deviceId}");
            Session = await Client.AuthenticateDeviceAsync(deviceId, create: true);
            Debug.Log($"[Nakama] 登录成功！UserId: {Session.UserId}，Token 过期时间: {Session.ExpireTime}");

            // 建立实时通信 Socket
            await ConnectSocketAsync();

            OnLoginSuccess?.Invoke(Session);
        }
        catch (Exception e)
        {
            Debug.LogError($"[Nakama] 登录失败：{e.Message}");
            OnLoginFailure?.Invoke(e);
        }
    }

    /// <summary>
    /// 建立 WebSocket 实时连接。
    /// </summary>
    private async Task ConnectSocketAsync()
    {
        Socket = Client.NewSocket(useMainThread: true);
        Socket.Connected += () => Debug.Log("[Nakama] Socket 已连接");
        Socket.Closed += reason => Debug.Log($"[Nakama] Socket 已断开，原因：{reason}");
        Socket.ReceivedError += e => Debug.LogError($"[Nakama] Socket 错误：{e.Message}");

        await Socket.ConnectAsync(Session, appearOnline: true);
        Debug.Log("[Nakama] Socket 连接成功");
    }

    /// <summary>
    /// 获取或创建持久化的设备唯一 ID。
    /// </summary>
    private static string GetOrCreateDeviceId()
    {
        const string key = "nakama_device_id";
        if (!PlayerPrefs.HasKey(key))
        {
            PlayerPrefs.SetString(key, SystemInfo.deviceUniqueIdentifier != SystemInfo.unsupportedIdentifier
                ? SystemInfo.deviceUniqueIdentifier
                : Guid.NewGuid().ToString());
            PlayerPrefs.Save();
        }
        return PlayerPrefs.GetString(key);
    }

    private async void OnDestroy()
    {
        if (Socket != null)
        {
            await Socket.CloseAsync();
        }
    }
}
