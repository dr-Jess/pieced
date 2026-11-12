using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using UnityEngine;
using UnityEngine.InputSystem;

namespace Pieced.Networking
{
    /// <summary>
    /// TCP server the phone app (and later the CV pipeline) connects to. Each client sends newline-delimited JSON;
    /// sockets are read on background threads and messages are parsed and dispatched on the main thread in Update.
    /// </summary>
    [DefaultExecutionOrder(-100)] // drain the network before input providers and punch controllers run
    public sealed class MotionStreamServer : MonoBehaviour
    {
        [Tooltip("0 lets the OS pick a free port (used by tests); see BoundPort.")]
        [SerializeField] private int port = 7777;
        [SerializeField] private bool showStatus = true;

        public event Action<MotionSample> SampleReceived;
        public event Action<PoseSample> PoseReceived;
        public event Action<StreamClientInfo> ClientConnected;
        public event Action<StreamClientInfo> ClientDisconnected;

        public int Port { get => port; set { if (listener != null) throw new InvalidOperationException("Server already running."); port = value; } }
        public int BoundPort { get; private set; }
        public bool IsRunning => listener != null;
        public IReadOnlyCollection<StreamClientInfo> Clients => clients.Values;
        public string LastError { get; private set; }

        private enum EventKind { Connected, Line, Disconnected, Browser }
        private readonly struct NetEvent
        {
            public readonly EventKind kind; public readonly int clientId; public readonly string payload;
            public NetEvent(EventKind kind, int clientId, string payload) { this.kind = kind; this.clientId = clientId; this.payload = payload; }
        }

        private readonly ConcurrentQueue<NetEvent> inbox = new ConcurrentQueue<NetEvent>();
        private readonly Dictionary<int, TcpClient> sockets = new Dictionary<int, TcpClient>(); // guarded by itself
        private readonly Dictionary<int, StreamClientInfo> clients = new Dictionary<int, StreamClientInfo>(); // main thread only
        private readonly Dictionary<int, (float windowStart, int count)> rateWindows = new Dictionary<int, (float, int)>();
        private TcpListener listener;
        private Thread acceptThread;
        private volatile bool running;
        private int nextClientId;
        private GUIStyle style;
        private InputAction toggleAction;

        private void Awake()
        {
            toggleAction = new InputAction("Toggle Stream Status", InputActionType.Button, "<Keyboard>/f2");
            toggleAction.performed += _ => showStatus = !showStatus;
        }

        private void OnEnable() { toggleAction?.Enable(); StartServer(); }
        private void OnDisable() { toggleAction?.Disable(); StopServer(); }
        private void OnDestroy() => toggleAction?.Dispose();

        private void StartServer()
        {
            try
            {
                listener = new TcpListener(IPAddress.Any, port);
                listener.Start();
                BoundPort = ((IPEndPoint)listener.LocalEndpoint).Port;
                running = true;
                var accepting = listener;
                acceptThread = new Thread(() => AcceptLoop(accepting)) { IsBackground = true, Name = "Pieced Stream Accept" };
                acceptThread.Start();
                LastError = null;
                Debug.Log($"[MotionStream] Listening on port {BoundPort}. Phone should connect to {LocalAddressHint()}:{BoundPort}");
            }
            catch (SocketException e)
            {
                LastError = e.Message;
                listener = null;
                Debug.LogError($"[MotionStream] Could not listen on port {port}: {e.Message}", this);
            }
        }

        private void StopServer()
        {
            running = false;
            listener?.Stop();
            listener = null;
            lock (sockets)
            {
                foreach (var socket in sockets.Values) socket.Close();
                sockets.Clear();
            }
            acceptThread?.Join(500);
            acceptThread = null;
            while (inbox.TryDequeue(out _)) { }
            foreach (var info in clients.Values) ClientDisconnected?.Invoke(info);
            clients.Clear();
            rateWindows.Clear();
        }

        private void AcceptLoop(TcpListener accepting)
        {
            while (running)
            {
                TcpClient socket;
                try { socket = accepting.AcceptTcpClient(); }
                catch (Exception) { return; } // listener stopped
                socket.NoDelay = true;
                int id = Interlocked.Increment(ref nextClientId);
                lock (sockets) sockets[id] = socket;
                inbox.Enqueue(new NetEvent(EventKind.Connected, id, socket.Client.RemoteEndPoint?.ToString()));
                new Thread(() => ReadLoop(id, socket)) { IsBackground = true, Name = $"Pieced Stream Client {id}" }.Start();
            }
        }

        private void ReadLoop(int id, TcpClient socket)
        {
            try
            {
                var stream = socket.GetStream();
                using var reader = new StreamReader(stream, Encoding.UTF8);
                string line = reader.ReadLine();
                if (line != null && IsHttpRequest(line))
                {
                    // Someone opened the address in a browser: answer so it doubles as a reachability check.
                    var body = $"Pieced motion stream is reachable on port {BoundPort}.\nConnect with the Pieced phone app, not a browser.\n";
                    var response = "HTTP/1.1 200 OK\r\nContent-Type: text/plain; charset=utf-8\r\nConnection: close\r\n" +
                                   $"Content-Length: {Encoding.UTF8.GetByteCount(body)}\r\n\r\n{body}";
                    var bytes = Encoding.UTF8.GetBytes(response);
                    stream.Write(bytes, 0, bytes.Length);
                    inbox.Enqueue(new NetEvent(EventKind.Browser, id, null));
                    return;
                }
                for (; running && line != null; line = reader.ReadLine())
                    if (line.Length > 0) inbox.Enqueue(new NetEvent(EventKind.Line, id, line));
            }
            catch (Exception) { } // socket closed or reset
            finally
            {
                lock (sockets) sockets.Remove(id);
                socket.Close();
                if (running) inbox.Enqueue(new NetEvent(EventKind.Disconnected, id, null));
            }
        }

        private void Update()
        {
            double now = Time.realtimeSinceStartupAsDouble;
            while (inbox.TryDequeue(out var e))
            {
                switch (e.kind)
                {
                    case EventKind.Connected:
                    {
                        var info = new StreamClientInfo { Id = e.clientId, Endpoint = e.payload };
                        clients[e.clientId] = info;
                        rateWindows[e.clientId] = (Time.realtimeSinceStartup, 0);
                        Debug.Log($"[MotionStream] Client {e.clientId} connected from {e.payload}");
                        ClientConnected?.Invoke(info);
                        break;
                    }
                    case EventKind.Disconnected:
                        if (clients.Remove(e.clientId, out var gone))
                        {
                            rateWindows.Remove(e.clientId);
                            Debug.Log($"[MotionStream] Client {e.clientId} ({gone.Role} {gone.Name}) disconnected after {gone.SamplesReceived} samples");
                            ClientDisconnected?.Invoke(gone);
                        }
                        break;
                    case EventKind.Browser:
                        if (clients.Remove(e.clientId, out var browser))
                        {
                            rateWindows.Remove(e.clientId);
                            Debug.Log($"[MotionStream] Browser at {browser.Endpoint} checked the server (reachable). Use the phone app to stream.");
                        }
                        break;
                    case EventKind.Line:
                        if (clients.TryGetValue(e.clientId, out var client)) HandleLine(client, e.payload, now);
                        break;
                }
            }
            UpdateRates();
        }

        private void HandleLine(StreamClientInfo client, string line, double now)
        {
            WireMessage message;
            try { message = JsonUtility.FromJson<WireMessage>(line); }
            catch (Exception) { Debug.LogWarning($"[MotionStream] Client {client.Id} sent invalid JSON: {Truncate(line)}"); return; }
            if (message == null) return;

            switch (message.type)
            {
                case "hello":
                    client.Role = string.IsNullOrEmpty(message.role) ? "unknown" : message.role;
                    client.Name = message.name ?? "";
                    Debug.Log($"[MotionStream] Client {client.Id} is {client.Role} '{client.Name}' (protocol {message.protocol})");
                    break;
                case "motion":
                    if (client.LastSequence >= 0 && message.seq > client.LastSequence + 1)
                        client.SamplesDropped += message.seq - client.LastSequence - 1;
                    client.LastSequence = message.seq;
                    client.SamplesReceived++;
                    var window = rateWindows[client.Id];
                    rateWindows[client.Id] = (window.windowStart, window.count + 1);
                    SampleReceived?.Invoke(new MotionSample(client.Id, client.Role, message.seq, message.t, now,
                        new Vector3(message.ax, message.ay, message.az),
                        new Vector3(message.gx, message.gy, message.gz),
                        new Quaternion(message.qx, message.qy, message.qz, message.qw),
                        new Vector3(message.grx, message.gry, message.grz)));
                    break;
                case "pose":
                    if (message.world == null || message.world.Length < 18) break;
                    client.SamplesReceived++;
                    var poseWindow = rateWindows[client.Id];
                    rateWindows[client.Id] = (poseWindow.windowStart, poseWindow.count + 1);
                    var points = new Vector3[6];
                    for (int i = 0; i < 6; i++)
                        points[i] = new Vector3(message.world[3 * i], message.world[3 * i + 1], message.world[3 * i + 2]);
                    PoseReceived?.Invoke(new PoseSample(client.Id, message.seq, message.t, now, points,
                        message.vis != null && message.vis.Length >= 6 ? message.vis : new[] { 1f, 1f, 1f, 1f, 1f, 1f }));
                    break;
                default:
                    Debug.LogWarning($"[MotionStream] Client {client.Id} sent unknown message type '{message.type}'");
                    break;
            }
        }

        private void UpdateRates()
        {
            float now = Time.realtimeSinceStartup;
            foreach (var info in clients.Values)
            {
                var (start, count) = rateWindows[info.Id];
                if (now - start < 1f) continue;
                info.SampleRate = count / (now - start);
                rateWindows[info.Id] = (now, 0);
            }
        }

        private void OnGUI()
        {
            if (!showStatus) return;
            style ??= new GUIStyle(GUI.skin.label) { fontSize = 14, normal = { textColor = Color.white } };
            var text = new StringBuilder();
            if (!IsRunning) text.Append($"Motion stream: NOT LISTENING ({LastError})");
            else
            {
                text.Append($"Motion stream: {LocalAddressHint()}:{BoundPort}  [F2]");
                if (clients.Count == 0) text.Append("\n  waiting for phone...");
                foreach (var c in clients.Values)
                    text.Append($"\n  #{c.Id} {c.Role} {c.Name}: {c.SampleRate:0} Hz, {c.SamplesReceived} rx, {c.SamplesDropped} dropped");
            }
            var content = new GUIContent(text.ToString());
            var size = style.CalcSize(content);
            var rect = new Rect(Screen.width - size.x - 12, 8, size.x + 4, size.y + 4);
            GUI.color = new Color(0, 0, 0, 0.55f);
            GUI.DrawTexture(rect, Texture2D.whiteTexture);
            GUI.color = Color.white;
            GUI.Label(new Rect(rect.x + 2, rect.y + 2, size.x, size.y), content, style);
        }

        private static string cachedAddress;

        /// <summary>Best guess at this machine's LAN IPv4 address, for telling the phone where to connect.</summary>
        public static string LocalAddressHint()
        {
            if (cachedAddress != null) return cachedAddress;
            try
            {
                // No packets are sent: connecting a UDP socket just asks the OS which interface it would route through.
                using var probe = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
                probe.Connect("8.8.8.8", 53);
                cachedAddress = ((IPEndPoint)probe.LocalEndPoint).Address.ToString();
            }
            catch (Exception) { cachedAddress = "<laptop IP>"; }
            return cachedAddress;
        }

        private static bool IsHttpRequest(string line) =>
            line.Contains(" HTTP/") && (line.StartsWith("GET ") || line.StartsWith("HEAD ") || line.StartsWith("POST "));

        private static string Truncate(string s) => s.Length <= 120 ? s : s.Substring(0, 120) + "...";
    }
}
