using System;
using System.Diagnostics;
using System.IO;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Threading;
using GTFO_VR.Core;

namespace PSVR2Toolkit.CAPI {
    /// <summary>
    /// Client for the PSVR2 Toolkit legacy TCP IPC (protocol v1, port 3364). The server is built into
    /// Toolkit v0.2.x drivers; for v1.0.0 experimental builds it is the standalone
    /// psvr2_toolkit_legacy_ipc_server, which still accepts v1 clients. GTFO VR only uses it as a
    /// fallback for adaptive triggers when the direct CAPI is unavailable.
    /// </summary>
    public class IpcClient {
        private const ushort IPC_SERVER_PORT = 3364;
        private const ushort k_unIpcVersion = 1;

        private static IpcClient m_pInstance;

        private readonly object m_sendLock = new object();
        private volatile bool m_running;
        private TcpClient m_client;
        private NetworkStream m_stream;
        private Thread m_receiveThread;
        private CancellationTokenSource m_shutdownToken;

        public bool IsRunning => m_running;

        public static IpcClient Instance() {
            if ( m_pInstance == null ) {
                m_pInstance = new IpcClient();
            }
            return m_pInstance;
        }

        public bool Start() {
            if ( m_running ) {
                return true;
            }

            // Release anything left over from a connection the server dropped.
            Stop();

            try {
                m_client = new TcpClient();
                m_client.Connect("127.0.0.1", IPC_SERVER_PORT);
                if ( !m_client.Connected ) {
                    Stop();
                    return false;
                }

                m_stream = m_client.GetStream();
                m_shutdownToken = new CancellationTokenSource();
                m_running = true;

                CancellationToken token = m_shutdownToken.Token;
                m_receiveThread = new Thread(() => ReceiveLoop(token)) {
                    IsBackground = true,
                    Name = "GTFO VR PSVR2 IPC"
                };
                m_receiveThread.Start();
                return true;
            } catch ( SocketException ex ) {
                Log.Debug($"PSVR2 Toolkit legacy IPC connection failed: {ex.SocketErrorCode}");
                Stop();
                return false;
            }
        }

        public void Stop() {
            m_running = false;

            try {
                m_shutdownToken?.Cancel();
            } catch ( ObjectDisposedException ) { }

            try {
                m_stream?.Close();
                m_client?.Close();
            } catch { }

            Thread receiveThread = m_receiveThread;
            if ( receiveThread != null && receiveThread.IsAlive && receiveThread != Thread.CurrentThread ) {
                receiveThread.Join(2000);
            }
            m_receiveThread = null;

            m_stream?.Dispose();
            m_stream = null;
            m_client?.Dispose();
            m_client = null;
            m_shutdownToken?.Dispose();
            m_shutdownToken = null;
        }

        private void ReceiveLoop(CancellationToken token) {
            byte[] buffer = new byte[1024];

            try {
                Socket clientSocket = m_client.Client;
                m_stream.ReadTimeout = 1; // make the underlying stream non-blocking

                CommandDataClientRequestHandshake clientHandshakeRequest = new CommandDataClientRequestHandshake() {
                    ipcVersion = k_unIpcVersion,
                    processId = ( uint ) Process.GetCurrentProcess().Id
                };
                SendIpcCommand(ECommandType.ClientRequestHandshake, clientHandshakeRequest);

                while ( m_running && !token.IsCancellationRequested ) {
                    bool readable = clientSocket.Poll(1000 /* 1ms */, SelectMode.SelectRead);
                    if ( readable ) {
                        int available = clientSocket.Available;
                        if ( available == 0 ) {
                            // Readable with nothing to read: the server closed the connection.
                            Log.Warning("PSVR2 Toolkit legacy IPC disconnected from server.");
                            break;
                        }

                        if ( available > buffer.Length ) {
                            buffer = new byte[Math.Max(available, buffer.Length * 2)];
                        }

                        int bytesRead = m_stream.Read(buffer, 0, Math.Min(buffer.Length, available));
                        if ( bytesRead <= 0 ) {
                            Log.Warning("PSVR2 Toolkit legacy IPC disconnected from server.");
                            break;
                        }

                        if ( bytesRead < Marshal.SizeOf<CommandHeader>() ) {
                            Log.Warning("PSVR2 Toolkit legacy IPC received invalid command header size.");
                            continue;
                        }

                        HandleIpcCommand(buffer, bytesRead);
                        continue;
                    }

                    Thread.Sleep(1);
                }
            } catch ( OperationCanceledException ) {
                // Shutdown.
            } catch ( ObjectDisposedException ) {
                // Shutdown closed the socket under us.
            } catch ( Exception ex ) {
                if ( m_running ) {
                    Log.Warning($"PSVR2 Toolkit legacy IPC receive loop error: {ex.Message}");
                }
            } finally {
                // Let the output worker see the dropped connection and reconnect.
                m_running = false;
            }
        }

        private void HandleIpcCommand(byte[] pBuffer, int bytesReceived) {
            CommandHeader header = ByteArrayToStructure<CommandHeader>(pBuffer, 0);

            switch ( header.type ) {
                case ECommandType.ServerPong: {
                        Log.Debug("PSVR2 Toolkit legacy IPC received pong.");
                        break;
                    }

                case ECommandType.ServerHandshakeResult: {
                        if ( header.dataLen == Marshal.SizeOf<CommandDataServerHandshakeResult>() ) {
                            CommandDataServerHandshakeResult response = ByteArrayToStructure<CommandDataServerHandshakeResult>(pBuffer, Marshal.SizeOf<CommandHeader>());
                            switch ( response.result ) {
                                case EHandshakeResult.Success: {
                                        Log.Info($"PSVR2 Toolkit legacy IPC handshake accepted (server IPC version {response.ipcVersion}).");
                                        break;
                                    }
                                case EHandshakeResult.Failed: {
                                        Log.Warning("PSVR2 Toolkit legacy IPC handshake failed.");
                                        break;
                                    }
                                case EHandshakeResult.Outdated: {
                                        Log.Warning($"PSVR2 Toolkit legacy IPC handshake failed: outdated client. Server IPC version {response.ipcVersion}");
                                        break;
                                    }
                            }
                        }
                        break;
                    }
            }
        }

        private void SendIpcCommand<T>(ECommandType type, T data) where T : struct {
            int headerSize = Marshal.SizeOf<CommandHeader>();
            int dataLen = Marshal.SizeOf<T>();
            byte[] buffer = new byte[headerSize + dataLen];

            CommandHeader header = new CommandHeader {
                type = type,
                dataLen = dataLen
            };

            IntPtr scratch = Marshal.AllocHGlobal(Math.Max(headerSize, dataLen));
            try {
                Marshal.StructureToPtr(header, scratch, false);
                Marshal.Copy(scratch, buffer, 0, headerSize);
                Marshal.StructureToPtr(data, scratch, false);
                Marshal.Copy(scratch, buffer, headerSize, dataLen);
            } finally {
                Marshal.FreeHGlobal(scratch);
            }

            Write(buffer);
        }

        private void Write(byte[] buffer) {
            // The receive thread (handshake) and the output worker both send; keep messages whole.
            lock ( m_sendLock ) {
                NetworkStream stream = m_stream;
                if ( !m_running || stream == null ) {
                    throw new IOException("PSVR2 Toolkit legacy IPC is not connected.");
                }

                stream.Write(buffer, 0, buffer.Length);
            }
        }

        private T ByteArrayToStructure<T>(byte[] bytes, int offset) where T : struct {
            int size = Marshal.SizeOf<T>();
            if ( size > bytes.Length - offset ) {
                throw new ArgumentException("Byte array is too small to contain the structure.");
            }

            IntPtr ptr = Marshal.AllocHGlobal(size);
            try {
                Marshal.Copy(bytes, offset, ptr, size);
                return Marshal.PtrToStructure<T>(ptr);
            } finally {
                Marshal.FreeHGlobal(ptr);
            }
        }

        public void TriggerEffectDisable(EVRControllerType controllerType) {
            SendIpcCommand(ECommandType.ClientTriggerEffectOff, new CommandDataClientTriggerEffectOff() {
                controllerType = controllerType
            });
        }

        public void TriggerEffectFeedback(EVRControllerType controllerType, byte position, byte strength) {
            SendIpcCommand(ECommandType.ClientTriggerEffectFeedback, new CommandDataClientTriggerEffectFeedback() {
                controllerType = controllerType,
                position = position,
                strength = strength,
            });
        }

        public void TriggerEffectWeapon(EVRControllerType controllerType, byte startPosition, byte endPosition, byte strength) {
            SendIpcCommand(ECommandType.ClientTriggerEffectWeapon, new CommandDataClientTriggerEffectWeapon() {
                controllerType = controllerType,
                startPosition = startPosition,
                endPosition = endPosition,
                strength = strength,
            });
        }

        public void TriggerEffectVibration(EVRControllerType controllerType, byte position, byte amplitude, byte frequency) {
            SendIpcCommand(ECommandType.ClientTriggerEffectVibration, new CommandDataClientTriggerEffectVibration() {
                controllerType = controllerType,
                position = position,
                amplitude = amplitude,
                frequency = frequency,
            });
        }

        public void TriggerEffectMultiplePositionFeedback(EVRControllerType controllerType, byte[] strength) {
            SendIpcCommand(ECommandType.ClientTriggerEffectMultiplePositionFeedback, new CommandDataClientTriggerEffectMultiplePositionFeedback() {
                controllerType = controllerType,
                strength = strength,
            });
        }

        public void TriggerEffectSlopeFeedback(EVRControllerType controllerType, byte startPosition, byte endPosition, byte startStrength, byte endStrength) {
            SendIpcCommand(ECommandType.ClientTriggerEffectSlopeFeedback, new CommandDataClientTriggerEffectSlopeFeedback() {
                controllerType = controllerType,
                startPosition = startPosition,
                endPosition = endPosition,
                startStrength = startStrength,
                endStrength = endStrength,
            });
        }

        public void TriggerEffectMultiplePositionVibration(EVRControllerType controllerType, byte frequency, byte[] amplitude) {
            SendIpcCommand(ECommandType.ClientTriggerEffectMultiplePositionVibration, new CommandDataClientTriggerEffectMultiplePositionVibration() {
                controllerType = controllerType,
                frequency = frequency,
                amplitude = amplitude,
            });
        }
    }
}
