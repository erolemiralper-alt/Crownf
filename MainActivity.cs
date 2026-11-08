using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using System.Threading;
using Android.App;
using Android.Bluetooth;
using Android.Content;
using Android.Content.PM;
using Android.Graphics;
using Android.Opengl;
using Android.OS;
using Android.Views;
using Android.Widget;
using Java.Nio;
using Java.Util;
using Javax.Microedition.Khronos.Egl;
using Javax.Microedition.Khronos.Opengles;
using Matrix = Android.Opengl.Matrix;
using Math = System.Math;
using EGLConfig = Javax.Microedition.Khronos.Egl.EGLConfig;

namespace CrownDriftWatch
{
    public enum GameState { MENU, LOBBY, PLAYING, PAUSED, WIN, GAME_OVER, GARAGE, SETTINGS }
    public enum PowerUpType { None = 0, Missile = 1, Spike = 2, Shield = 3, FrontWeapon = 4, BadBoxSlow = 5 }

    [Activity(
        Label = "Crown Drift Co-op",
        MainLauncher = true,
        ConfigurationChanges = ConfigChanges.Orientation | ConfigChanges.ScreenSize | ConfigChanges.KeyboardHidden,
        ScreenOrientation = ScreenOrientation.Portrait)]
    public class MainActivity : Activity
    {
        private GLSurfaceView glView;
        private HudOverlayView hudView;
        private CrownDriftEngine engine;
        private BluetoothCoopDiscoveryReceiver btReceiver;

        protected override void OnCreate(Bundle savedInstanceState)
        {
            base.OnCreate(savedInstanceState);
            RequestWindowFeature(WindowFeatures.NoTitle);
            Window.AddFlags(WindowManagerFlags.Fullscreen | WindowManagerFlags.KeepScreenOn);

            RequestBluetoothPermissionsIfNeeded();

            engine = new CrownDriftEngine(this);

            FrameLayout root = new FrameLayout(this);
            glView = new GLSurfaceView(this);
            glView.SetEGLContextClientVersion(2);
            glView.SetEGLConfigChooser(8, 8, 8, 8, 16, 0);
            glView.SetRenderer(engine);
            glView.RenderMode = Rendermode.Continuously;

            hudView = new HudOverlayView(this, engine);
            engine.AttachViews(glView, hudView);

            root.AddView(glView, new FrameLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.MatchParent));
            root.AddView(hudView, new FrameLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.MatchParent));
            SetContentView(root);

            // Eşleştirmesiz Bluetooth Co-op otomatik cihaz tarayıcı
            btReceiver = new BluetoothCoopDiscoveryReceiver(engine);
            IntentFilter filter = new IntentFilter(BluetoothDevice.ActionFound);
            if ((int)Build.VERSION.SdkInt >= 33)
                RegisterReceiver(btReceiver, filter, ReceiverFlags.Exported);
            else
                RegisterReceiver(btReceiver, filter);
        }

        private void RequestBluetoothPermissionsIfNeeded()
        {
            if ((int)Build.VERSION.SdkInt >= 31)
            {
                string[] perms = {
                    "android.permission.BLUETOOTH_SCAN",
                    "android.permission.BLUETOOTH_CONNECT",
                    "android.permission.BLUETOOTH_ADVERTISE",
                    "android.permission.ACCESS_FINE_LOCATION"
                };
                RequestPermissions(perms, 101);
            }
            else if ((int)Build.VERSION.SdkInt >= 23)
            {
                RequestPermissions(new[] { "android.permission.ACCESS_FINE_LOCATION" }, 102);
            }
        }

        // Galaxy Watch 8 Döner Bezel / Crown Girişi
        public override bool OnGenericMotionEvent(MotionEvent e)
        {
            if (e.Action == MotionEventActions.Scroll && e.IsFromSource(InputSourceType.RotaryEncoder))
            {
                float delta = -e.GetAxisValue(Axis.Vscroll);
                engine.HandleCrownRotation(delta);
                return true;
            }
            return base.OnGenericMotionEvent(e);
        }

        public override void OnBackPressed()
        {
            if (!engine.OnBackPressed())
                base.OnBackPressed();
        }

        protected override void OnPause()
        {
            base.OnPause();
            glView?.OnPause();
            engine?.OnPause();
        }

        protected override void OnResume()
        {
            base.OnResume();
            glView?.OnResume();
        }

        protected override void OnDestroy()
        {
            try { UnregisterReceiver(btReceiver); } catch { }
            engine?.Dispose();
            base.OnDestroy();
        }
    }

    // =========================================================================
    // EŞLEŞTİRMESİZ 4 KİŞİLİK BLUETOOTH CO-OP TARAYICI VE YÖNETİCİ
    // =========================================================================
    public class BluetoothCoopDiscoveryReceiver : BroadcastReceiver
    {
        private readonly CrownDriftEngine engine;
        public BluetoothCoopDiscoveryReceiver(CrownDriftEngine eng) { engine = eng; }

        public override void OnReceive(Context context, Intent intent)
        {
            if (BluetoothDevice.ActionFound.Equals(intent.Action))
            {
                BluetoothDevice device = (BluetoothDevice)intent.GetParcelableExtra(BluetoothDevice.ExtraDevice);
                if (device != null)
                {
                    engine.CoopManager.TryAutoConnectDiscoveredDevice(device);
                }
            }
        }
    }

    public struct PeerCarState
    {
        public bool Active;
        public byte CarId;
        public float X, Z, Yaw, Roll, VX, VZ, RaceProgress;
        public byte TriggeredPowerUp;
        public byte IsExploded;
    }

    public class BluetoothCoopManager : IDisposable
    {
        private static readonly UUID COOP_UUID = UUID.FromString("b4e8a910-7c2d-4e5b-9a11-cf8092837465");
        private const string SERVICE_NAME = "CrownDriftWatchCoop";
        public const int MAX_PLAYERS = 4;

        private readonly CrownDriftEngine engine;
        private readonly BluetoothAdapter adapter;
        private BluetoothServerSocket serverSocket;
        private readonly List<BluetoothSocket> sockets = new List<BluetoothSocket>();
        private readonly HashSet<string> attemptedAddresses = new HashSet<string>();
        private volatile bool isRunning;

        public bool IsMultiplayerActive => isRunning && sockets.Count > 0;
        public bool IsHost { get; private set; }
        public int LocalCarId { get; private set; } = 0;
        public int ConnectedCount => sockets.Count + 1;
        public string StatusText { get; private set; } = "Hazır";

        public readonly PeerCarState[] RemotePeers = new PeerCarState[MAX_PLAYERS];
        private readonly byte[] sendBuf = new byte[28];

        public BluetoothCoopManager(CrownDriftEngine eng)
        {
            engine = eng;
            adapter = BluetoothAdapter.DefaultAdapter;
        }

        public void StartHostLobby()
        {
            if (adapter == null || !adapter.IsEnabled) { StatusText = "BT Kapalı!"; return; }
            StopLobby();
            IsHost = true;
            LocalCarId = 0;
            isRunning = true;
            StatusText = "Lobi Açık (1/4) - Bekleniyor...";

            ThreadPool.QueueUserWorkItem(_ =>
            {
                try
                {
                    serverSocket = adapter.ListenUsingInsecureRfcommWithServiceRecord(SERVICE_NAME, COOP_UUID);
                    while (isRunning && sockets.Count < MAX_PLAYERS - 1)
                    {
                        BluetoothSocket s = serverSocket.Accept();
                        if (s != null)
                        {
                            int assignedId;
                            lock (sockets)
                            {
                                sockets.Add(s);
                                assignedId = sockets.Count;
                            }
                            s.OutputStream.WriteByte((byte)assignedId);
                            StatusText = $"Lobi ({ConnectedCount}/4 Oyuncu)";
                            engine.RequestUiRefresh();
                            StartReceiveThread(s);
                        }
                    }
                }
                catch { }
            });
        }

        public void StartAutoJoinScan()
        {
            if (adapter == null || !adapter.IsEnabled) { StatusText = "BT Kapalı!"; return; }
            StopLobby();
            IsHost = false;
            isRunning = true;
            attemptedAddresses.Clear();
            StatusText = "Çevredeki Lobi Taranıyor...";

            try
            {
                if (adapter.BondedDevices != null)
                {
                    foreach (var dev in adapter.BondedDevices)
                        TryAutoConnectDiscoveredDevice(dev);
                }
                if (adapter.IsDiscovering) adapter.CancelDiscovery();
                adapter.StartDiscovery();
            }
            catch { }
        }

        public void TryAutoConnectDiscoveredDevice(BluetoothDevice device)
        {
            if (!isRunning || IsHost || sockets.Count > 0 || device == null) return;
            lock (attemptedAddresses)
            {
                if (!attemptedAddresses.Add(device.Address)) return;
            }

            ThreadPool.QueueUserWorkItem(_ =>
            {
                try
                {
                    // Eşleştirme / PIN gerektirmeyen Insecure RFCOMM bağlantısı
                    BluetoothSocket s = device.CreateInsecureRfcommSocketToServiceRecord(COOP_UUID);
                    s.Connect();
                    int id = s.InputStream.ReadByte();
                    LocalCarId = Math.Clamp(id, 1, MAX_PLAYERS - 1);
                    lock (sockets) { sockets.Add(s); }
                    if (adapter.IsDiscovering) adapter.CancelDiscovery();
                    StatusText = $"Bağlandı! Oyuncu #{LocalCarId + 1}";
                    engine.RequestUiRefresh();
                    StartReceiveThread(s);
                }
                catch { }
            });
        }

        // Host yarışı başlattığında tüm bağlı oyuncuların yarışını otomatik başlatır
        public void BroadcastRaceStartCommand(int mapIndex)
        {
            if (!IsMultiplayerActive || !IsHost) return;
            byte[] startCmd = new byte[28];
            startCmd[0] = 0xFE; // Yarış başlatma komutu başlığı
            startCmd[1] = (byte)mapIndex;
            startCmd[27] = 0xAA;

            lock (sockets)
            {
                for (int i = sockets.Count - 1; i >= 0; i--)
                {
                    try { sockets[i].OutputStream.Write(startCmd, 0, 28); }
                    catch { sockets.RemoveAt(i); }
                }
            }
        }

        public void BroadcastLocalCar(CarEntity car, byte powerUpEvent)
        {
            if (!IsMultiplayerActive) return;

            sendBuf[0] = (byte)LocalCarId;
            WriteFloat(sendBuf, 1, car.X);
            WriteFloat(sendBuf, 5, car.Z);
            WriteFloat(sendBuf, 9, car.YawAngle);
            WriteFloat(sendBuf, 13, car.RollAngle);
            WriteFloat(sendBuf, 17, car.VelocityX);
            WriteFloat(sendBuf, 21, car.TotalRaceProgress);
            sendBuf[25] = powerUpEvent;
            sendBuf[26] = (byte)(car.IsExploded ? 1 : 0);
            sendBuf[27] = 0xAA;

            lock (sockets)
            {
                for (int i = sockets.Count - 1; i >= 0; i--)
                {
                    try { sockets[i].OutputStream.Write(sendBuf, 0, 28); }
                    catch { sockets.RemoveAt(i); }
                }
            }
        }

        private void StartReceiveThread(BluetoothSocket sock)
        {
            ThreadPool.QueueUserWorkItem(_ =>
            {
                byte[] buf = new byte[28];
                Stream ins = sock.InputStream;
                try
                {
                    while (isRunning && sock.IsConnected)
                    {
                        int read = 0;
                        while (read < 28)
                        {
                            int r = ins.Read(buf, read, 28 - read);
                            if (r <= 0) return;
                            read += r;
                        }

                        if (buf[27] != 0xAA) continue;

                        // Host'tan gelen Yarış Başlatma Komutu (0xFE)
                        if (buf[0] == 0xFE)
                        {
                            int mapIdx = Math.Clamp((int)buf[1], 0, 5);
                            engine.QueueStartRace(mapIdx);
                            continue;
                        }

                        byte id = buf[0];
                        if (id < MAX_PLAYERS)
                        {
                            RemotePeers[id] = new PeerCarState
                            {
                                Active = true,
                                CarId = id,
                                X = BitConverter.ToSingle(buf, 1),
                                Z = BitConverter.ToSingle(buf, 5),
                                Yaw = BitConverter.ToSingle(buf, 9),
                                Roll = BitConverter.ToSingle(buf, 13),
                                VX = BitConverter.ToSingle(buf, 17),
                                RaceProgress = BitConverter.ToSingle(buf, 21),
                                TriggeredPowerUp = buf[25],
                                IsExploded = buf[26]
                            };
                        }
                        if (IsHost)
                        {
                            lock (sockets)
                            {
                                foreach (var other in sockets)
                                {
                                    if (!ReferenceEquals(other, sock))
                                    {
                                        try { other.OutputStream.Write(buf, 0, 28); } catch { }
                                    }
                                }
                            }
                        }
                    }
                }
                catch { }
            });
        }

        private static void WriteFloat(byte[] b, int o, float v)
        {
            int bits = BitConverter.SingleToInt32Bits(v);
            b[o] = (byte)(bits & 0xFF);
            b[o + 1] = (byte)((bits >> 8) & 0xFF);
            b[o + 2] = (byte)((bits >> 16) & 0xFF);
            b[o + 3] = (byte)((bits >> 24) & 0xFF);
        }

        public void StopLobby()
        {
            isRunning = false;
            try { if (adapter != null && adapter.IsDiscovering) adapter.CancelDiscovery(); } catch { }
            try { serverSocket?.Close(); } catch { }
            lock (sockets)
            {
                foreach (var s in sockets) { try { s.Close(); } catch { } }
                sockets.Clear();
            }
            for (int i = 0; i < MAX_PLAYERS; i++) RemotePeers[i].Active = false;
        }

        public void Dispose() => StopLobby();
    }

    // =========================================================================
    // DAİRESEL TAMPONLU SKID TRAIL (LASTİK İZİ - SkidTrail.java UYARLAMASI)
    // =========================================================================
    public class SkidTrailMesh
    {
        private readonly int maxSegs;
        private readonly float[] verts;
        private readonly float[] strength;
        private readonly FloatBuffer vertexBuffer;
        private readonly ShortBuffer indexBuffer;

        private int head, count;
        private bool dirty;
        private readonly bool[] active = new bool[2];
        private readonly bool[] edgeValid = new bool[2];
        private readonly float[] lastX = new float[2], lastZ = new float[2], lastS = new float[2];
        private readonly float[] aX = new float[2], aZ = new float[2], bX = new float[2], bZ = new float[2];

        public SkidTrailMesh(int maxSegments)
        {
            maxSegs = maxSegments;
            verts = new float[maxSegs * 28];
            strength = new float[maxSegs * 4];

            ByteBuffer vbb = ByteBuffer.AllocateDirect(verts.Length * 4);
            vbb.Order(ByteOrder.NativeOrder());
            vertexBuffer = vbb.AsFloatBuffer();

            short[] indices = new short[maxSegs * 6];
            for (int i = 0; i < maxSegs; i++)
            {
                int v = i * 4, idx = i * 6;
                indices[idx] = (short)v;
                indices[idx + 1] = (short)(v + 1);
                indices[idx + 2] = (short)(v + 2);
                indices[idx + 3] = (short)(v + 2);
                indices[idx + 4] = (short)(v + 3);
                indices[idx + 5] = (short)v;
            }
            ByteBuffer ibb = ByteBuffer.AllocateDirect(indices.Length * 2);
            ibb.Order(ByteOrder.NativeOrder());
            indexBuffer = ibb.AsShortBuffer();
            indexBuffer.Put(indices);
            indexBuffer.Position(0);
        }

        public void Extend(int wheel, float x, float z, float skidStrength, float width = 0.34f)
        {
            if (!active[wheel])
            {
                if (skidStrength <= 0.02f) return;
                active[wheel] = true;
                edgeValid[wheel] = false;
                lastX[wheel] = x; lastZ[wheel] = z; lastS[wheel] = 0f;
                return;
            }

            float dx = x - lastX[wheel], dz = z - lastZ[wheel];
            float dist = (float)Math.Sqrt(dx * dx + dz * dz);
            if (dist > 3.0f) { active[wheel] = false; return; }
            if (dist < 0.05f) return;

            float ox = -dz / dist * width * 0.5f;
            float oz = dx / dist * width * 0.5f;

            if (!edgeValid[wheel])
            {
                aX[wheel] = lastX[wheel] - ox; aZ[wheel] = lastZ[wheel] - oz;
                bX[wheel] = lastX[wheel] + ox; bZ[wheel] = lastZ[wheel] + oz;
                edgeValid[wheel] = true;
            }

            float curAX = x - ox, curAZ = z - oz;
            float curBX = x + ox, curBZ = z + oz;

            int vOff = head * 28;
            int sOff = head * 4;
            SetVert(vOff, aX[wheel], 0.035f, aZ[wheel]);
            SetVert(vOff + 7, bX[wheel], 0.035f, bZ[wheel]);
            SetVert(vOff + 14, curBX, 0.035f, curBZ);
            SetVert(vOff + 21, curAX, 0.035f, curAZ);

            strength[sOff] = lastS[wheel];
            strength[sOff + 1] = lastS[wheel];
            strength[sOff + 2] = skidStrength;
            strength[sOff + 3] = skidStrength;

            head = (head + 1) % maxSegs;
            if (count < maxSegs) count++;

            aX[wheel] = curAX; aZ[wheel] = curAZ;
            bX[wheel] = curBX; bZ[wheel] = curBZ;
            lastX[wheel] = x; lastZ[wheel] = z; lastS[wheel] = skidStrength;

            if (skidStrength <= 0.02f) active[wheel] = false;
            dirty = true;
        }

        private void SetVert(int o, float x, float y, float z)
        {
            verts[o] = x; verts[o + 1] = y; verts[o + 2] = z;
            verts[o + 3] = 0.07f; verts[o + 4] = 0.07f; verts[o + 5] = 0.07f; verts[o + 6] = 0.5f;
        }

        public void BreakStrips() { active[0] = false; active[1] = false; }

        public void Render(int posLoc, int colLoc)
        {
            if (count == 0) return;
            if (dirty)
            {
                for (int i = 0; i < count; i++)
                {
                    int segIdx = (head - 1 - i + maxSegs) % maxSegs;
                    float t = (float)segIdx / maxSegs;
                    float fade = (1f - t * t) * 0.62f;
                    int vOff = i * 28, sOff = i * 4;
                    for (int j = 0; j < 4; j++)
                    {
                        verts[vOff + j * 7 + 6] = strength[sOff + j] * fade;
                    }
                }
                vertexBuffer.Position(0);
                vertexBuffer.Put(verts, 0, count * 28);
                dirty = false;
            }

            vertexBuffer.Position(0);
            GLES20.GlVertexAttribPointer(posLoc, 3, GLES20.GlFloat, false, 28, vertexBuffer);
            vertexBuffer.Position(3);
            GLES20.GlVertexAttribPointer(colLoc, 4, GLES20.GlFloat, false, 28, vertexBuffer);
            indexBuffer.Position(0);
            GLES20.GlDrawElements(GLES20.GlTriangles, count * 6, GLES20.GlUnsignedShort, indexBuffer);
        }
    }

    // =========================================================================
    // ARAÇ VARLIĞI: KADEMELİ HASAR -> ALEV ÇIKMASI -> HAVAYA UÇARAK PATLAMA
    // =========================================================================
    public class CarEntity
    {
        public const float STEP = 1f / 60f;
        public const float ACCELERATION = 0.36f;

        public int CarId;
        public bool IsPlayer;
        public Vector3 BodyColor;
        public bool IsSportModel;

        // Konum ve Hız (Car3D.java)
        public float X, Y = 0.25f, Z;
        public float YawAngle, RollAngle, PitchAngle;
        public float VelocityX, VelocityY, VelocityZ;
        public float Grip = 0.012f;
        public float SpeedFactor = 1.0f;
        public float SmoothedSteering;
        public float FrontWheelAngleDeg;

        // İnterpolasyon
        private float prevX, prevY, prevZ, prevYaw, prevRoll, prevPitch;
        public float RenderX, RenderY = 0.25f, RenderZ, RenderYaw, RenderRoll, RenderPitch;

        // Drift ve Arka Tekerlekler
        public bool IsCurrentlyDrifting;
        public float DriftAmount;
        public float SkidIntensity;
        public float RearLX, RearLZ, RearRX, RearRZ;
        public float HoodX, HoodZ;
        public SkidTrailMesh Trail;

        // Kademeli Hasar -> Önce Alev Çıkması -> Sonra Havaya Uçup Patlama!
        public float Health = 100f;
        public float CriticalFireTimer = 0f; // Can 0'a indiğinde önce 1.1 sn boyunca alev çıkar, sonra havaya uçup patlar!
        public bool IsBurning => !IsExploded && (Health < 40f || CriticalFireTimer > 0f);
        public bool IsSmokingDamage => !IsExploded && Health < 75f;
        public bool IsExploded;
        public float RespawnTimer;       // 3.0 saniyelik canlanma süresi
        public float InvulnerableTimer;  // Yeniden doğuş sonrası koruma
        public float OverturnTimer;      // Füze/Diken devrilme süresi
        public float CollisionCooldown;

        // Tur, Sıralama, Puan ve Boost
        public int LapsCompleted;
        public int NearestWaypointIndex;
        public float TotalRaceProgress;
        public bool FinishedRace;
        public int LockedFinishRank;
        public bool OnGrass;
        public bool EdgeActive;
        public float BoostMeter;
        public bool BoostReady;
        public float BoostTime;
        public float ComboTime, ComboGap;
        public int Multiplier = 1;
        public float SessionScore;

        // Sürpriz Kutular (Power-Up)
        public PowerUpType ActivePowerUp = PowerUpType.None;
        public float PowerUpTimer;
        public float WeaponFireCooldown;

        // Bot Yapay Zekası Hata Değişkenleri
        private float aiPhase;
        private float aiSteerError;
        private float aiErrorTimer;

        public CarEntity(int id, bool isPlayer, Vector3 color, bool sport, int trailSegs)
        {
            CarId = id;
            IsPlayer = isPlayer;
            BodyColor = color;
            IsSportModel = sport;
            aiPhase = id * 1.9f;
            Trail = new SkidTrailMesh(trailSegs);
        }

        public void BeginStep()
        {
            prevX = X; prevY = Y; prevZ = Z;
            prevYaw = YawAngle; prevRoll = RollAngle; prevPitch = PitchAngle;
        }

        public void Steer(float input)
        {
            if (IsExploded || OverturnTimer > 0f) return;
            SmoothedSteering += (input - SmoothedSteering) * 0.015f;
            YawAngle += 1.3f * SmoothedSteering;
            RollAngle += ((-SmoothedSteering * 70f) - RollAngle) * 0.1f;
        }

        public float ComputeBotSteering(TrackData track)
        {
            int n = track.CenterPoints.Count;
            if (n == 0 || IsExploded) return 0f;

            aiPhase += STEP;
            aiErrorTimer -= STEP;
            if (aiErrorTimer <= 0f)
            {
                aiErrorTimer = 1.6f + (float)Math.Abs(Math.Sin(aiPhase * 1.5f)) * 2.0f;
                bool makeMistake = Math.Sin(aiPhase * 2.9f + CarId * 53) > 0.74;
                aiSteerError = makeMistake ? (float)Math.Cos(aiPhase * 4.3f) * 0.024f : 0f;
            }

            int targetIdx = (NearestWaypointIndex + 6) % n;
            Vector2 targetPt = track.CenterPoints[targetIdx];
            float desiredYaw = (float)Math.Atan2(-(targetPt.X - X), -(targetPt.Y - Z));
            float diff = desiredYaw - YawAngle;
            while (diff > Math.PI) diff -= (float)(2.0 * Math.PI);
            while (diff < -Math.PI) diff += (float)(2.0 * Math.PI);

            if (BoostReady && Math.Abs(diff) < 0.22f)
            {
                BoostReady = false;
                BoostMeter = 0f;
                BoostTime = 3.0f;
            }

            return Math.Clamp(diff * 0.12f, -0.055f, 0.055f) + aiSteerError;
        }

        public void UpdatePhysics(TrackData track, ParticleAndDebrisSystem particles)
        {
            if (CollisionCooldown > 0f) CollisionCooldown -= STEP;
            if (InvulnerableTimer > 0f) InvulnerableTimer -= STEP;

            // 1. HAVAYA UÇARAK PATLAMA FİZİĞİ VE 3 SANİYE SONRA YOLDA DOĞUŞ
            if (IsExploded)
            {
                Trail.BreakStrips();
                VelocityY -= 26.0f * STEP;
                X += VelocityX * STEP;
                Y += VelocityY * STEP;
                Z += VelocityZ * STEP;

                if (Y > 0.3f)
                {
                    PitchAngle += 360f * STEP;
                    RollAngle += 450f * STEP;
                }
                else
                {
                    Y = 0.25f;
                    if (Math.Abs(VelocityY) > 2.0f)
                    {
                        // Yere çarptıkça etrafa enkaz parçaları saçılır
                        particles.SpawnBouncingGroundDebris(X, 0.3f, Z, 5, BodyColor);
                    }
                    VelocityY = Math.Abs(VelocityY) * 0.38f;
                    VelocityX *= 0.82f;
                    VelocityZ *= 0.82f;
                }

                particles.SpawnFireOrSmoke(X, Y + 0.3f, Z, 0f, 0f, isFire: true);

                RespawnTimer -= STEP;
                if (RespawnTimer <= 0f)
                {
                    // Binanın içinde değil, patlama noktasına en yakın YOL MERKEZİNDE doğ!
                    track.GetNearestSafeRoadPose(X, Z, out float rx, out float rz, out float ryaw);
                    SetPose(rx, rz, ryaw, 0f, snap: true);
                    Health = 100f;
                    CriticalFireTimer = 0f;
                    IsExploded = false;
                    InvulnerableTimer = 2.5f;
                }
                return;
            }

            // 2. KRİTİK ALEV EVRESİ: Can 0'a indiğinde önce yoğun alev çıkar, hemen ardından havaya uçup patlar!
            if (CriticalFireTimer > 0f)
            {
                CriticalFireTimer -= STEP;
                particles.SpawnFireOrSmoke(HoodX, 0.6f, HoodZ, VelocityX * 0.3f, VelocityZ * 0.3f, isFire: true);
                particles.SpawnFireOrSmoke(X, 0.5f, Z, 0f, 0f, isFire: true);
                if (CriticalFireTimer <= 0f)
                {
                    TriggerAirborneExplosion(particles);
                    return;
                }
            }

            // Füze veya Diken İsabetiyle Devrilme
            if (OverturnTimer > 0f)
            {
                OverturnTimer -= STEP;
                VelocityX *= 0.92f;
                VelocityZ *= 0.92f;
                X += VelocityX;
                Z += VelocityZ;
                RollAngle += 22f;
                Trail.BreakStrips();
                return;
            }

            if (PowerUpTimer > 0f)
            {
                PowerUpTimer -= STEP;
                if (PowerUpTimer <= 0f) ActivePowerUp = PowerUpType.None;
            }

            // Yoldan çıkınca oyun bitmez! Çim (OnGrass) %40 yavaşlatır (SpeedFactor = 0.60f)
            if (BoostTime > 0f)
            {
                BoostTime -= STEP;
                SpeedFactor = 1.22f;
            }
            else if (CriticalFireTimer > 0f)
            {
                SpeedFactor = 0.45f; // Alev almış araç patlamadan hemen önce tekler ve yavaşlar
            }
            else if (ActivePowerUp == PowerUpType.BadBoxSlow)
            {
                SpeedFactor = 0.55f;
            }
            else if (OnGrass)
            {
                SpeedFactor = 0.60f;
            }
            else
            {
                SpeedFactor = 1.0f;
            }

            // Referans Car3D.update() Hız Vektörü Yakınsaması
            float fwdX = -(float)Math.Sin(YawAngle);
            float fwdZ = -(float)Math.Cos(YawAngle);
            float thrust = ACCELERATION * SpeedFactor;

            VelocityX += (fwdX * thrust - VelocityX) * Grip;
            VelocityZ += (fwdZ * thrust - VelocityZ) * Grip;
            X += VelocityX;
            Z += VelocityZ;
            Y = 0.25f;
            PitchAngle *= 0.85f;

            float speedSq = VelocityX * VelocityX + VelocityZ * VelocityZ;

            // Arka tekerlekler ve motor kaputu koordinatları
            float sin = (float)Math.Sin(YawAngle);
            float cos = (float)Math.Cos(YawAngle);
            float rearCx = X + sin * 0.8f;
            float rearCz = Z + cos * 0.8f;
            float offX = cos * 0.55f;
            float offZ = -sin * 0.55f;
            RearLX = rearCx + offX; RearLZ = rearCz + offZ;
            RearRX = rearCx - offX; RearRZ = rearCz - offZ;
            HoodX = X - sin * 0.85f; HoodZ = Z - cos * 0.85f;

            // Kademeli Hasar Görsel Efektleri: Önce Duman, Sonra Alev!
            if (IsBurning)
            {
                particles.SpawnFireOrSmoke(HoodX, 0.55f, HoodZ, VelocityX, VelocityZ, isFire: true);
            }
            else if (IsSmokingDamage && Random.Shared.Next(2) == 0)
            {
                particles.SpawnFireOrSmoke(HoodX, 0.55f, HoodZ, VelocityX, VelocityZ, isFire: false);
            }

            // Histerezisli Drift Tespiti (0.955f / 0.93f)
            bool wasDrifting = IsCurrentlyDrifting;
            IsCurrentlyDrifting = false;
            DriftAmount = 0f;
            float drift = 0f;

            if (speedSq > 1.0E-4f)
            {
                float speed = (float)Math.Sqrt(speedSq);
                float alignment = (VelocityX / speed) * fwdX + (VelocityZ / speed) * fwdZ;
                drift = Math.Clamp(1f - alignment, 0f, 1f);
                DriftAmount = drift;
                float threshold = wasDrifting ? 0.955f : 0.93f;
                if (speed > 0.06f && alignment < threshold)
                {
                    IsCurrentlyDrifting = true;
                }
            }

            float targetSkid = 0f;
            if (speedSq > 0.0036f)
            {
                targetSkid = Math.Clamp((drift - 0.015f) / 0.07f, 0f, 1f);
            }
            SkidIntensity += (targetSkid - SkidIntensity) * 0.25f;

            Trail.Extend(0, RearLX, RearLZ, SkidIntensity);
            Trail.Extend(1, RearRX, RearRZ, SkidIntensity);

            RollAngle *= 0.85f;
        }

        // Objelere/Binalara/Engellere Çarpınca: Yere Parçacık Düşür + Kademeli Hasar Ver + En Son Alev ve Patlama
        public void ApplyObstacleCollision(float obsX, float obsZ, float damageAmount, ParticleAndDebrisSystem particles)
        {
            if (IsExploded || InvulnerableTimer > 0f || CollisionCooldown > 0f) return;
            CollisionCooldown = 0.42f;

            // Her çarpışmada yere seken 3D araç parçacıkları ve kıvılcımlar düşür!
            particles.SpawnBouncingGroundDebris(X, 0.45f, Z, count: 12, BodyColor);

            // Çarpışma tepkisi (Sekme)
            float dx = X - obsX, dz = Z - obsZ;
            float len = Math.Max((float)Math.Sqrt(dx * dx + dz * dz), 0.01f);
            X += (dx / len) * 0.95f;
            Z += (dz / len) * 0.95f;
            VelocityX = (dx / len) * 0.14f;
            VelocityZ = (dz / len) * 0.14f;

            if (ActivePowerUp == PowerUpType.Shield)
            {
                ActivePowerUp = PowerUpType.None;
                PowerUpTimer = 0f;
                return;
            }

            // Eğer araç zaten kritik alev aşamasındayken bir kez daha çarparsa anında havaya uçup patlasın!
            if (CriticalFireTimer > 0f)
            {
                TriggerAirborneExplosion(particles);
                return;
            }

            // Kademeli hasar azaltımı (100 -> 72 Duman -> 40 Alev -> 0 Kritik Alev & Havaya Uçup Patlama)
            Health -= damageAmount;

            if (Health <= 0f)
            {
                Health = 0f;
                // Önce 1.1 saniye boyunca şiddetli alev çıkarır, ardından havaya uçarak patlar!
                CriticalFireTimer = 1.1f;
                particles.SpawnBouncingGroundDebris(X, 0.5f, Z, count: 14, new Vector3(1.0f, 0.45f, 0.05f));
            }
        }

        public void TriggerAirborneExplosion(ParticleAndDebrisSystem particles)
        {
            if (IsExploded) return;
            Health = 0f;
            CriticalFireTimer = 0f;
            IsExploded = true;
            RespawnTimer = 3.0f; // 3 saniyelik yeniden doğma süresi

            // Aracı yüksekten havaya fırlat ve takla attır!
            VelocityY = 15.5f;
            VelocityX = ((float)Random.Shared.NextDouble() - 0.5f) * 6.5f;
            VelocityZ = ((float)Random.Shared.NextDouble() - 0.5f) * 6.5f;

            SmoothedSteering = 0f;
            IsCurrentlyDrifting = false;
            DriftAmount = 0f;
            SkidIntensity = 0f;
            ComboTime = 0f;
            Multiplier = 1;
            Trail.BreakStrips();

            // Büyük alev patlaması ve yere saçılan 32 adet 3D enkaz parçası!
            particles.SpawnExplosionBurst(X, Y + 0.4f, Z, BodyColor);
        }

        public void ApplyInterpolated(float alpha)
        {
            RenderX = prevX + (X - prevX) * alpha;
            RenderY = prevY + (Y - prevY) * alpha;
            RenderZ = prevZ + (Z - prevZ) * alpha;
            RenderYaw = prevYaw + (YawAngle - prevYaw) * alpha;
            RenderRoll = prevRoll + (RollAngle - prevRoll) * alpha;
            RenderPitch = prevPitch + (PitchAngle - prevPitch) * alpha;
            FrontWheelAngleDeg = Math.Clamp(SmoothedSteering * 500f, -28f, 28f);
        }

        public void SetPose(float px, float pz, float yaw, float roll, bool snap)
        {
            BeginStep();
            X = px; Y = 0.25f; Z = pz;
            YawAngle = yaw; RollAngle = roll; PitchAngle = 0f;
            VelocityX = 0f; VelocityY = 0f; VelocityZ = 0f;
            if (snap) BeginStep();
            Trail.BreakStrips();
        }
    }

    // =========================================================================
    // NESNE HAVUZLU (ZERO-GC) DUMAN, ALEV VE YERE DÜŞÜP SEKEN 3D ENKAZ SİSTEMİ
    // =========================================================================
    public struct SmokeFireParticle
    {
        public bool Active;
        public float X, Y, Z, VX, VY, VZ;
        public float Life, MaxLife, Size;
        public float R, G, B;
    }

    public struct GroundDebrisPiece
    {
        public bool Active;
        public float X, Y, Z, VX, VY, VZ;
        public float RotAngle, RotSpeed, Size, Life;
        public float R, G, B;
    }

    public class ParticleAndDebrisSystem
    {
        public const int MAX_SMOKE_FIRE = 64;
        public const int MAX_DEBRIS = 64;

        public readonly SmokeFireParticle[] SmokePool = new SmokeFireParticle[MAX_SMOKE_FIRE];
        public readonly GroundDebrisPiece[] DebrisPool = new GroundDebrisPiece[MAX_DEBRIS];
        private int nextSmokeIdx = 0;
        private int nextDebrisIdx = 0;

        public void Clear()
        {
            for (int i = 0; i < MAX_SMOKE_FIRE; i++) SmokePool[i].Active = false;
            for (int i = 0; i < MAX_DEBRIS; i++) DebrisPool[i].Active = false;
        }

        public void SpawnTireSmoke(float x, float z, float vx, float vz, bool dust, int mapType)
        {
            int idx = nextSmokeIdx;
            nextSmokeIdx = (nextSmokeIdx + 1) % MAX_SMOKE_FIRE;

            float life = (dust ? 0.7f : 1.0f) * ((float)Random.Shared.NextDouble() * 0.3f + 0.85f);
            float r = 0.88f, g = 0.88f, b = 0.9f;
            if (mapType == 4) { r = 0.95f; g = 0.97f; b = 1.0f; }
            else if (dust) { r = 0.55f; g = 0.45f; b = 0.32f; }
            else if (mapType == 3) { r = 0.55f; g = 0.85f; b = 1.0f; }
            else if (mapType == 5) { r = 0.6f; g = 0.55f; b = 0.7f; }

            SmokePool[idx] = new SmokeFireParticle
            {
                Active = true,
                X = x + ((float)Random.Shared.NextDouble() - 0.5f) * 0.5f,
                Y = 0.45f,
                Z = z + ((float)Random.Shared.NextDouble() - 0.5f) * 0.5f,
                VX = -vx * 12f,
                VY = 1.3f,
                VZ = -vz * 12f,
                Life = life,
                MaxLife = life,
                Size = dust ? 0.65f : 0.50f,
                R = r, G = g, B = b
            };
        }

        public void SpawnFireOrSmoke(float x, float y, float z, float vx, float vz, bool isFire)
        {
            int idx = nextSmokeIdx;
            nextSmokeIdx = (nextSmokeIdx + 1) % MAX_SMOKE_FIRE;

            bool yellowFlame = Random.Shared.Next(2) == 0;
            SmokePool[idx] = new SmokeFireParticle
            {
                Active = true,
                X = x + ((float)Random.Shared.NextDouble() - 0.5f) * 0.4f,
                Y = y,
                Z = z + ((float)Random.Shared.NextDouble() - 0.5f) * 0.4f,
                VX = -vx * 5f + ((float)Random.Shared.NextDouble() - 0.5f) * 1.5f,
                VY = isFire ? 3.4f : 1.8f,
                VZ = -vz * 5f + ((float)Random.Shared.NextDouble() - 0.5f) * 1.5f,
                Life = isFire ? 0.38f : 0.65f,
                MaxLife = isFire ? 0.38f : 0.65f,
                Size = isFire ? 0.46f : 0.50f,
                R = isFire ? 1.0f : 0.20f,
                G = isFire ? (yellowFlame ? 0.78f : 0.28f) : 0.20f,
                B = isFire ? 0.05f : 0.20f
            };
        }

        // Çarpışma anında yere düşüp asfaltta seken 3D araç parçacıkları
        public void SpawnBouncingGroundDebris(float x, float y, float z, int count, Vector3 carColor)
        {
            for (int i = 0; i < count; i++)
            {
                int idx = nextDebrisIdx;
                nextDebrisIdx = (nextDebrisIdx + 1) % MAX_DEBRIS;

                double ang = Random.Shared.NextDouble() * Math.PI * 2.0;
                float spd = (float)Random.Shared.NextDouble() * 8.0f + 2.5f;
                bool isSpark = (i % 3 == 0);
                bool isDarkPart = (i % 3 == 1);

                DebrisPool[idx] = new GroundDebrisPiece
                {
                    Active = true,
                    X = x,
                    Y = y + 0.2f,
                    Z = z,
                    VX = (float)Math.Cos(ang) * spd,
                    VY = (float)Random.Shared.NextDouble() * 7.5f + 3.5f,
                    VZ = (float)Math.Sin(ang) * spd,
                    RotAngle = (float)Random.Shared.NextDouble() * 360f,
                    RotSpeed = ((float)Random.Shared.NextDouble() - 0.5f) * 540f,
                    Size = isSpark ? 0.15f : 0.24f,
                    Life = 3.4f,
                    R = isSpark ? 1.0f : (isDarkPart ? 0.15f : carColor.X),
                    G = isSpark ? 0.76f : (isDarkPart ? 0.15f : carColor.Y),
                    B = isSpark ? 0.10f : (isDarkPart ? 0.15f : carColor.Z)
                };
            }
        }

        public void SpawnExplosionBurst(float x, float y, float z, Vector3 carColor)
        {
            SpawnBouncingGroundDebris(x, y, z, count: 32, carColor);
            for (int i = 0; i < 18; i++)
            {
                SpawnFireOrSmoke(
                    x + ((float)Random.Shared.NextDouble() - 0.5f) * 1.5f,
                    y + (float)Random.Shared.NextDouble() * 0.9f,
                    z + ((float)Random.Shared.NextDouble() - 0.5f) * 1.5f,
                    0f, 0f, isFire: true);
            }
        }

        public void Update(float dt)
        {
            for (int i = 0; i < MAX_SMOKE_FIRE; i++)
            {
                if (!SmokePool[i].Active) continue;
                SmokePool[i].Life -= dt;
                if (SmokePool[i].Life <= 0f) { SmokePool[i].Active = false; continue; }

                SmokePool[i].X += SmokePool[i].VX * dt;
                SmokePool[i].Y += SmokePool[i].VY * dt;
                SmokePool[i].Z += SmokePool[i].VZ * dt;
                SmokePool[i].VX *= 0.96f;
                SmokePool[i].VZ *= 0.96f;
            }

            for (int i = 0; i < MAX_DEBRIS; i++)
            {
                if (!DebrisPool[i].Active) continue;
                DebrisPool[i].Life -= dt;
                if (DebrisPool[i].Life <= 0f) { DebrisPool[i].Active = false; continue; }

                DebrisPool[i].VY -= 28.0f * dt;
                DebrisPool[i].X += DebrisPool[i].VX * dt;
                DebrisPool[i].Y += DebrisPool[i].VY * dt;
                DebrisPool[i].Z += DebrisPool[i].VZ * dt;
                DebrisPool[i].RotAngle += DebrisPool[i].RotSpeed * dt;

                if (DebrisPool[i].Y <= 0.06f)
                {
                    DebrisPool[i].Y = 0.06f;
                    DebrisPool[i].VY = -DebrisPool[i].VY * 0.48f;
                    DebrisPool[i].VX *= 0.76f;
                    DebrisPool[i].VZ *= 0.76f;
                    DebrisPool[i].RotSpeed *= 0.7f;
                }
            }
        }
    }

    // =========================================================================
    // PİST VERİSİ VE GEOMETRİSİ (Track3D.java - 6 HARİTA + BAŞLANGIÇ KEMERİ)
    // =========================================================================
    public struct SceneryObstacle
    {
        public float X, Y, Z, ScaleX, ScaleY, ScaleZ, YawDeg, Radius;
        public Vector3 Color;
        public bool Collidable;
    }

    public class TrackData
    {
        public readonly int MapType;
        public readonly bool IsHighQuality;
        public readonly List<Vector2> CenterPoints = new List<Vector2>();
        public readonly List<SceneryObstacle> Scenery = new List<SceneryObstacle>();
        public bool[] ZoneMask = Array.Empty<bool>();

        public float StartX, StartZ, StartAngle;
        public int TargetLaps = 3;
        private int finishedCarCounter = 0;

        public float TrackWidth => MapType == 3 ? 6.5f : 5.5f;
        public float CurbEdge => TrackWidth + 0.8f;

        private static readonly float[] CYBER_POINTS = {
            -30.0f, 60.0f, -26.02f, 60.39f, -22.08f, 60.67f, -18.16f, 60.84f,
            -14.25f, 60.91f, -10.34f, 60.91f, -6.42f, 60.85f, -2.49f, 60.74f,
            1.48f, 60.6f, 5.49f, 60.45f, 9.55f, 60.29f, 13.67f, 60.15f,
            17.87f, 60.04f, 22.15f, 59.97f, 26.53f, 59.96f, 31.01f, 60.02f,
            35.58f, 60.13f, 40.14f, 60.19f, 44.58f, 60.08f, 48.83f, 59.68f,
            52.76f, 58.89f, 56.28f, 57.58f, 59.3f, 55.64f, 61.71f, 52.97f,
            63.51f, 49.64f, 64.74f, 45.78f, 65.43f, 41.54f, 65.62f, 37.06f,
            65.34f, 32.48f, 64.62f, 27.96f, 63.5f, 23.6f, 61.97f, 19.53f,
            60.07f, 15.85f, 57.79f, 12.68f, 55.15f, 10.11f, 52.16f, 8.24f,
            48.85f, 6.97f, 45.25f, 6.19f, 41.37f, 5.78f, 37.25f, 5.6f,
            32.91f, 5.55f, 28.37f, 5.49f, 23.67f, 5.3f, 18.82f, 4.87f,
            13.91f, 4.17f, 9.11f, 3.45f, 4.61f, 2.94f, 0.61f, 2.89f,
            -2.71f, 3.55f, -5.17f, 5.18f, -6.68f, 7.91f, -7.55f, 11.53f,
            -8.15f, 15.77f, -8.84f, 20.36f, -10.01f, 25.03f, -11.92f, 29.51f,
            -14.49f, 33.51f, -17.53f, 36.78f, -20.84f, 39.03f, -24.26f, 39.99f,
            -27.6f, 39.43f, -30.75f, 37.49f, -33.61f, 34.45f, -36.1f, 30.61f,
            -38.12f, 26.26f, -39.6f, 21.7f, -40.45f, 17.21f, -40.67f, 12.9f,
            -40.31f, 8.76f, -39.43f, 4.8f, -38.09f, 0.98f, -36.34f, -2.68f,
            -34.23f, -6.21f, -31.83f, -9.58f, -29.13f, -12.73f, -26.18f, -15.6f,
            -22.98f, -18.13f, -19.57f, -20.23f, -15.96f, -21.89f, -12.18f, -23.15f,
            -8.26f, -24.04f, -4.23f, -24.64f, -0.11f, -24.99f, 4.06f, -25.16f,
            8.27f, -25.18f, 12.48f, -25.13f, 16.67f, -25.05f, 20.8f, -24.99f,
            24.87f, -25.0f, 28.89f, -25.06f, 32.87f, -25.14f, 36.84f, -25.24f,
            40.82f, -25.32f, 44.83f, -25.38f, 48.88f, -25.38f, 52.99f, -25.31f,
            57.19f, -25.16f, 61.49f, -24.9f, 65.88f, -24.57f, 70.27f, -24.33f,
            74.59f, -24.32f, 78.76f, -24.68f, 82.7f, -25.58f, 86.33f, -27.16f,
            89.57f, -29.57f, 92.31f, -32.9f, 94.33f, -36.85f, 95.33f, -40.99f,
            95.04f, -44.86f, 93.27f, -48.1f, 90.27f, -50.71f, 86.43f, -52.77f,
            82.12f, -54.37f, 77.72f, -55.6f, 73.39f, -56.51f, 69.15f, -57.13f,
            64.99f, -57.51f, 60.88f, -57.67f, 56.83f, -57.65f, 52.82f, -57.47f,
            48.85f, -57.16f, 44.9f, -56.77f, 40.97f, -56.31f, 37.05f, -55.83f,
            33.12f, -55.35f, 29.18f, -54.91f, 25.23f, -54.53f, 21.25f, -54.19f,
            17.26f, -53.89f, 13.25f, -53.63f, 9.23f, -53.38f, 5.19f, -53.14f,
            1.14f, -52.9f, -2.92f, -52.65f, -6.99f, -52.38f, -11.07f, -52.08f,
            -15.15f, -51.74f, -19.25f, -51.34f, -23.35f, -50.89f, -27.45f, -50.37f,
            -31.56f, -49.76f, -35.66f, -49.06f, -39.72f, -48.22f, -43.73f, -47.22f,
            -47.65f, -46.02f, -51.45f, -44.59f, -55.12f, -42.89f, -58.62f, -40.91f,
            -61.93f, -38.59f, -65.03f, -35.95f, -67.91f, -33.02f, -70.55f, -29.84f,
            -72.94f, -26.43f, -75.06f, -22.82f, -76.9f, -19.05f, -78.46f, -15.16f,
            -79.7f, -11.16f, -80.63f, -7.11f, -81.23f, -3.01f, -81.53f, 1.12f,
            -81.52f, 5.25f, -81.21f, 9.37f, -80.6f, 13.46f, -79.72f, 17.5f,
            -78.55f, 21.49f, -77.11f, 25.39f, -75.4f, 29.19f, -73.44f, 32.88f,
            -71.22f, 36.43f, -68.77f, 39.81f, -66.1f, 43.0f, -63.2f, 45.96f,
            -60.11f, 48.69f, -56.82f, 51.14f, -53.35f, 53.3f, -49.71f, 55.13f,
            -45.92f, 56.63f, -42.02f, 57.82f, -38.03f, 58.75f, -34.01f, 59.46f
        };

        public TrackData(int mapType, bool hq)
        {
            MapType = mapType;
            IsHighQuality = hq;
            BuildCenterPoints();
            ComputeDriftZones();
            StartX = CenterPoints[0].X;
            StartZ = CenterPoints[0].Y;
            Vector2 p0 = CenterPoints[0], p1 = CenterPoints[1];
            StartAngle = (float)Math.Atan2(-(p1.X - p0.X), -(p1.Y - p0.Y));
            GenerateStartArchAndBuildings();
        }

        public void ResetFinishOrder() => finishedCarCounter = 0;

        private void BuildCenterPoints()
        {
            if (MapType == 3)
            {
                int stride = IsHighQuality ? 2 : 4;
                for (int i = 0; i < CYBER_POINTS.Length; i += stride)
                    CenterPoints.Add(new Vector2(CYBER_POINTS[i], CYBER_POINTS[i + 1]));
                return;
            }

            int step = IsHighQuality ? 2 : 5;
            for (int deg = 0; deg < 360; deg += step)
            {
                double t = deg * Math.PI / 180.0;
                float r;
                switch (MapType)
                {
                    case 0: r = (float)Math.Cos(t * 3.0) * 3f + 31f; break;
                    case 1: r = 35f - (float)Math.Cos(t * 2.0) * 15f; break;
                    case 2: r = (float)Math.Sin(t * 3.0) * 15f + 40f; break;
                    case 4: r = (float)Math.Sin(t * 2.0) * 10f + 42f - (float)Math.Cos(t * 3.0) * 5f; break;
                    case 5: r = (float)Math.Cos(t * 3.0) * 8f + 40f + (float)Math.Sin(t * 2.0) * 6f; break;
                    default: r = 30f; break;
                }
                CenterPoints.Add(new Vector2((float)Math.Cos(t) * r, (float)Math.Sin(t) * r));
            }
        }

        public Vector2 GetNormal(int idx)
        {
            int n = CenterPoints.Count;
            Vector2 prev = CenterPoints[(idx - 1 + n) % n];
            Vector2 cur = CenterPoints[idx];
            Vector2 next = CenterPoints[(idx + 1) % n];

            float dx1 = cur.X - prev.X, dy1 = cur.Y - prev.Y;
            float l1 = Math.Max((float)Math.Sqrt(dx1 * dx1 + dy1 * dy1), 0.001f);
            float n1x = -dy1 / l1, n1y = dx1 / l1;

            float dx2 = next.X - cur.X, dy2 = next.Y - cur.Y;
            float l2 = Math.Max((float)Math.Sqrt(dx2 * dx2 + dy2 * dy2), 0.001f);
            float n2x = -dy2 / l2, n2y = dx2 / l2;

            float sx = n2x + n1x, sy = n2y + n1y;
            float len = (float)Math.Sqrt(sx * sx + sy * sy);
            return len == 0f ? new Vector2(n1x, n1y) : new Vector2(sx / len, sy / len);
        }

        private void ComputeDriftZones()
        {
            int n = CenterPoints.Count;
            ZoneMask = new bool[n];
            if (n < 40) return;

            float[] turn = new float[n];
            for (int i = 0; i < n; i++)
            {
                Vector2 p0 = CenterPoints[(i - 1 + n) % n];
                Vector2 p1 = CenterPoints[i];
                Vector2 p2 = CenterPoints[(i + 1) % n];
                double a1 = Math.Atan2(p1.Y - p0.Y, p1.X - p0.X);
                double a2 = Math.Atan2(p2.Y - p1.Y, p2.X - p1.X);
                double d = a2 - a1;
                while (d > Math.PI) d -= 2 * Math.PI;
                while (d < -Math.PI) d += 2 * Math.PI;
                turn[i] = (float)Math.Abs(d);
            }

            int w = n / 24;
            float[] smooth = new float[n];
            for (int i = 0; i < n; i++)
            {
                float s = 0f;
                for (int k = -w; k <= w; k++) s += turn[(i + k + n) % n];
                smooth[i] = s;
            }

            bool[] used = new bool[n];
            for (int pass = 0; pass < 2; pass++)
            {
                int best = -1;
                for (int i = 0; i < n; i++)
                {
                    if (i < n / 10 || i > n - n / 10 || used[i]) continue;
                    if (best < 0 || smooth[i] > smooth[best]) best = i;
                }
                if (best < 0) break;
                for (int k = -w; k <= w; k++) ZoneMask[(best + k + n) % n] = true;
                for (int k = -n / 4; k <= n / 4; k++) used[(best + k + n) % n] = true;
            }
        }

        private void GenerateStartArchAndBuildings()
        {
            // 1. Track3D.java Başlangıç Kemeri (archPost + archBanner)
            Vector2 c0 = CenterPoints[0];
            Vector2 n0 = GetNormal(0);
            float archW = TrackWidth + 1.6f;
            float archAngle = (float)(Math.Atan2(-n0.Y, n0.X) * 180.0 / Math.PI);

            Scenery.Add(new SceneryObstacle
            {
                X = c0.X + n0.X * archW, Y = 2.6f, Z = c0.Y + n0.Y * archW,
                ScaleX = 0.45f, ScaleY = 5.2f, ScaleZ = 0.45f, YawDeg = 0f, Radius = 0.5f,
                Color = new Vector3(0.22f, 0.28f, 0.32f), Collidable = false
            });
            Scenery.Add(new SceneryObstacle
            {
                X = c0.X - n0.X * archW, Y = 2.6f, Z = c0.Y - n0.Y * archW,
                ScaleX = 0.45f, ScaleY = 5.2f, ScaleZ = 0.45f, YawDeg = 0f, Radius = 0.5f,
                Color = new Vector3(0.22f, 0.28f, 0.32f), Collidable = false
            });
            Scenery.Add(new SceneryObstacle
            {
                X = c0.X, Y = 5.0f, Z = c0.Y,
                ScaleX = archW * 2f + 0.4f, ScaleY = 0.9f, ScaleZ = 0.35f, YawDeg = archAngle, Radius = 0.5f,
                Color = new Vector3(0.85f, 0.16f, 0.22f), Collidable = false
            });

            // 2. Pist Kenarı Binalar ve Çarpılabilir Dekorlar
            System.Random rnd = new System.Random(MapType + 10);
            int target = IsHighQuality ? 42 : 22;
            for (int i = 0; i < target; i++)
            {
                double ang = rnd.NextDouble() * Math.PI * 2.0;
                float rad = (float)rnd.NextDouble() * 80f + 10f;
                float x = (float)Math.Cos(ang) * rad;
                float z = (float)Math.Sin(ang) * rad;

                float minD = float.MaxValue;
                foreach (var p in CenterPoints)
                {
                    float d = Vector2.Distance(p, new Vector2(x, z));
                    if (d < minD) minD = d;
                }
                if (minD < CurbEdge + 2.8f) continue;

                float h = (float)rnd.NextDouble() * 8f + 5f;
                Vector3 col = MapType == 3 ? new Vector3(0.1f, 0.7f, 0.9f)
                            : MapType == 5 ? new Vector3(0.85f, 0.4f, 0.1f)
                            : new Vector3(0.28f, 0.32f, 0.38f);
                Scenery.Add(new SceneryObstacle
                {
                    X = x, Y = h * 0.5f, Z = z,
                    ScaleX = 5.2f, ScaleY = h, ScaleZ = 5.2f, YawDeg = 0f,
                    Radius = 3.1f,
                    Color = col,
                    Collidable = true
                });
            }
        }

        public void GetNearestSafeRoadPose(float x, float z, out float roadX, out float roadZ, out float roadYaw)
        {
            int n = CenterPoints.Count;
            int best = 0;
            float bestD = float.MaxValue;
            for (int i = 0; i < n; i++)
            {
                float dx = CenterPoints[i].X - x;
                float dz = CenterPoints[i].Y - z;
                float d = dx * dx + dz * dz;
                if (d < bestD) { bestD = d; best = i; }
            }
            Vector2 cur = CenterPoints[best];
            Vector2 next = CenterPoints[(best + 1) % n];
            roadX = cur.X;
            roadZ = cur.Y;
            roadYaw = (float)Math.Atan2(-(next.X - cur.X), -(next.Y - cur.Y));
        }

        public void UpdateCarProgressAndSurface(CarEntity car, out bool inDriftZone)
        {
            int n = CenterPoints.Count;
            int best = car.NearestWaypointIndex;
            float bestD = float.MaxValue;

            for (int k = -15; k <= 15; k++)
            {
                int idx = (car.NearestWaypointIndex + k + n) % n;
                Vector2 p = CenterPoints[idx];
                float d = (p.X - car.X) * (p.X - car.X) + (p.Y - car.Z) * (p.Y - car.Z);
                if (d < bestD) { best = idx; bestD = d; }
            }
            if (bestD > 120f)
            {
                for (int i = 0; i < n; i++)
                {
                    Vector2 p = CenterPoints[i];
                    float d = (p.X - car.X) * (p.X - car.X) + (p.Y - car.Z) * (p.Y - car.Z);
                    if (d < bestD) { best = i; bestD = d; }
                }
            }

            int diff = best - car.NearestWaypointIndex;
            if (diff < -n / 2)
            {
                car.LapsCompleted++;
                if (!car.FinishedRace && car.LapsCompleted >= TargetLaps)
                {
                    car.FinishedRace = true;
                    finishedCarCounter++;
                    car.LockedFinishRank = finishedCarCounter;
                }
            }
            else if (diff > n / 2 && car.LapsCompleted > 0 && !car.FinishedRace)
            {
                car.LapsCompleted--;
            }

            car.NearestWaypointIndex = best;
            float dist = (float)Math.Sqrt(bestD);

            // Yoldan çıkınca oyun kesinlikle bitmez! Çim sadece %40 yavaşlık verir.
            car.OnGrass = dist > CurbEdge;
            car.EdgeActive = car.IsCurrentlyDrifting && !car.OnGrass && (dist > CurbEdge - 1.6f);
            inDriftZone = ZoneMask.Length > best && ZoneMask[best];

            Vector2 cPt = CenterPoints[best];
            Vector2 nPt = CenterPoints[(best + 1) % n];
            Vector2 seg = nPt - cPt;
            float segLenSq = seg.LengthSquared();
            float frac = segLenSq > 0.0001f
                ? Math.Clamp(Vector2.Dot(new Vector2(car.X - cPt.X, car.Z - cPt.Y), seg) / segLenSq, 0f, 0.999f)
                : 0f;

            car.TotalRaceProgress = car.LapsCompleted * n + best + frac;
        }
    }

    // =========================================================================
    // DİNAMİK HAREKETLİ ENGELLER, SABİT BARİYERLER VE SÜRPRİZ KUTU SİSTEMİ
    // =========================================================================
    public struct MysteryBoxItem { public bool Active; public float X, Z, RespawnTimer, RotAngle; }
    public struct ProjectileItem { public bool Active; public int OwnerId; public PowerUpType Type; public float X, Z, VX, VZ, Life; }

    public struct DynamicRoadObstacle
    {
        public bool Active;
        public bool IsFallingDynamic;
        public int WaypointIdx;
        public float LateralOffset, LateralSpeed; // Yolda sağa-sola kayan dinamik engel!
        public float X, Y, Z, VY, Radius, Life;
    }

    public class WorldObjectsManager
    {
        public readonly MysteryBoxItem[] Boxes = new MysteryBoxItem[10];
        public readonly ProjectileItem[] Projectiles = new ProjectileItem[20];
        public readonly DynamicRoadObstacle[] RoadObstacles = new DynamicRoadObstacle[16];
        private float dynamicSpawnTimer = 2.5f;

        public void SetupForTrack(TrackData track)
        {
            int n = track.CenterPoints.Count;
            for (int i = 0; i < Projectiles.Length; i++) Projectiles[i].Active = false;
            for (int i = 0; i < RoadObstacles.Length; i++) RoadObstacles[i].Active = false;

            // Sürpriz Kutular
            for (int i = 0; i < Boxes.Length; i++)
            {
                int wp = (i * (n / Boxes.Length) + 12) % n;
                Vector2 pt = track.CenterPoints[wp];
                Boxes[i] = new MysteryBoxItem { Active = true, X = pt.X, Z = pt.Y, RespawnTimer = 0f, RotAngle = i * 35f };
            }

            // İlk 8 slot: Sabit ve Yolda Sağa-Sola Kayarak Gezen Dinamik Bariyerler
            for (int i = 0; i < 8; i++)
            {
                int wp = (i * (n / 8) + 22) % n;
                Vector2 pt = track.CenterPoints[wp];
                Vector2 norm = track.GetNormal(wp);
                float side = (i % 2 == 0 ? 2.2f : -2.2f);
                bool isSlidingDynamic = (i % 2 == 1); // Tek indeksli engeller yol üzerinde sağa-sola hareket eder!

                RoadObstacles[i] = new DynamicRoadObstacle
                {
                    Active = true,
                    IsFallingDynamic = false,
                    WaypointIdx = wp,
                    LateralOffset = side,
                    LateralSpeed = isSlidingDynamic ? 2.6f : 0f,
                    X = pt.X + norm.X * side,
                    Y = 0.55f,
                    Z = pt.Y + norm.Y * side,
                    VY = 0f,
                    Radius = 1.15f,
                    Life = 9999f
                };
            }
        }

        public void Update(float dt, TrackData track, List<CarEntity> cars, ParticleAndDebrisSystem particles)
        {
            // Yolda rastgele doğan (gökten yola düşüp seken ve kayan) dinamik engeller (Slot 8..15)
            dynamicSpawnTimer -= dt;
            if (dynamicSpawnTimer <= 0f)
            {
                dynamicSpawnTimer = 3.0f;
                for (int i = 8; i < RoadObstacles.Length; i++)
                {
                    if (!RoadObstacles[i].Active)
                    {
                        int wp = Random.Shared.Next(8, track.CenterPoints.Count - 8);
                        Vector2 pt = track.CenterPoints[wp];
                        Vector2 norm = track.GetNormal(wp);
                        float offset = ((float)Random.Shared.NextDouble() - 0.5f) * (track.TrackWidth * 1.1f);
                        RoadObstacles[i] = new DynamicRoadObstacle
                        {
                            Active = true,
                            IsFallingDynamic = true,
                            WaypointIdx = wp,
                            LateralOffset = offset,
                            LateralSpeed = ((Random.Shared.Next(2) == 0) ? 1.8f : -1.8f),
                            X = pt.X + norm.X * offset,
                            Y = 12.0f,
                            Z = pt.Y + norm.Y * offset,
                            VY = -18.0f,
                            Radius = 1.05f,
                            Life = 11.0f
                        };
                        break;
                    }
                }
            }

            // Sürpriz kutular
            for (int i = 0; i < Boxes.Length; i++)
            {
                Boxes[i].RotAngle += 110f * dt;
                if (!Boxes[i].Active)
                {
                    Boxes[i].RespawnTimer -= dt;
                    if (Boxes[i].RespawnTimer <= 0f) Boxes[i].Active = true;
                    continue;
                }

                for (int c = 0; c < cars.Count; c++)
                {
                    CarEntity car = cars[c];
                    if (car.IsExploded) continue;
                    float dx = car.X - Boxes[i].X, dz = car.Z - Boxes[i].Z;
                    if (dx * dx + dz * dz < 3.4f)
                    {
                        Boxes[i].Active = false;
                        Boxes[i].RespawnTimer = 6.0f;
                        int pick = Random.Shared.Next(1, 6);
                        car.ActivePowerUp = (PowerUpType)pick;
                        car.PowerUpTimer = (car.ActivePowerUp == PowerUpType.BadBoxSlow) ? 3.5f : 6.5f;
                        if (car.ActivePowerUp == PowerUpType.BadBoxSlow)
                        {
                            car.ApplyObstacleCollision(Boxes[i].X, Boxes[i].Z, 22f, particles);
                        }
                        break;
                    }
                }
            }

            // Araçların aktif özelliklerini çalıştır (Füze, Diken, Ön Silah)
            for (int c = 0; c < cars.Count; c++)
            {
                CarEntity car = cars[c];
                if (car.IsExploded || car.ActivePowerUp == PowerUpType.None) continue;

                if (car.ActivePowerUp == PowerUpType.Missile)
                {
                    if (IsEnemyInFrontSight(car, cars))
                    {
                        FireProjectile(car, PowerUpType.Missile, speed: 44f, fwdOffset: 2.0f, life: 3.5f);
                        car.ActivePowerUp = PowerUpType.None;
                        car.PowerUpTimer = 0f;
                    }
                }
                else if (car.ActivePowerUp == PowerUpType.Spike)
                {
                    FireProjectile(car, PowerUpType.Spike, speed: 0f, fwdOffset: -2.2f, life: 9.0f);
                    car.ActivePowerUp = PowerUpType.None;
                    car.PowerUpTimer = 0f;
                }
                else if (car.ActivePowerUp == PowerUpType.FrontWeapon)
                {
                    car.WeaponFireCooldown -= dt;
                    if (car.WeaponFireCooldown <= 0f)
                    {
                        car.WeaponFireCooldown = 0.32f;
                        FireProjectile(car, PowerUpType.FrontWeapon, speed: 38f, fwdOffset: 1.9f, life: 1.2f);
                    }
                }
            }

            // Füzeler, Mermiler ve Dikenler
            for (int i = 0; i < Projectiles.Length; i++)
            {
                if (!Projectiles[i].Active) continue;
                Projectiles[i].Life -= dt;
                if (Projectiles[i].Life <= 0f) { Projectiles[i].Active = false; continue; }

                Projectiles[i].X += Projectiles[i].VX * dt;
                Projectiles[i].Z += Projectiles[i].VZ * dt;

                for (int c = 0; c < cars.Count; c++)
                {
                    CarEntity target = cars[c];
                    if (target.CarId == Projectiles[i].OwnerId || target.IsExploded) continue;
                    float dx = target.X - Projectiles[i].X, dz = target.Z - Projectiles[i].Z;
                    if (dx * dx + dz * dz < 2.8f)
                    {
                        if (target.ActivePowerUp == PowerUpType.Shield)
                        {
                            target.ActivePowerUp = PowerUpType.None;
                            target.PowerUpTimer = 0f;
                        }
                        else
                        {
                            target.OverturnTimer = 1.35f;
                            target.ApplyObstacleCollision(Projectiles[i].X, Projectiles[i].Z, 28f, particles);
                        }
                        Projectiles[i].Active = false;
                        break;
                    }
                }
            }

            // Sabit Bariyerler, Hareketli/Düşen Dinamik Engeller ve Çarpışma Kontrolü
            float maxLat = track.TrackWidth - 1.0f;
            for (int i = 0; i < RoadObstacles.Length; i++)
            {
                if (!RoadObstacles[i].Active) continue;

                if (RoadObstacles[i].IsFallingDynamic)
                {
                    RoadObstacles[i].Life -= dt;
                    if (RoadObstacles[i].Life <= 0f) { RoadObstacles[i].Active = false; continue; }
                                        if (RoadObstacles[i].Y > 0.55f)
                    {
                        RoadObstacles[i].Y += RoadObstacles[i].VY * dt;
                        if (RoadObstacles[i].Y <= 0.55f)
                        {
                            RoadObstacles[i].Y = 0.55f;
                            // Dinamik engel yola çarptığı anda asfalta parçacıklar saçar!
                            particles.SpawnBouncingGroundDebris(
                                RoadObstacles[i].X, 0.3f, RoadObstacles[i].Z,
                                count: 8, new Vector3(1.0f, 0.55f, 0.1f));
                        }
                    }
                }

                // Yerdeki dinamik engeller yolun sağından soluna doğru kayarak hareket eder
                if (RoadObstacles[i].Y <= 0.56f && Math.Abs(RoadObstacles[i].LateralSpeed) > 0.01f)
                {
                    RoadObstacles[i].LateralOffset += RoadObstacles[i].LateralSpeed * dt;
                    if (Math.Abs(RoadObstacles[i].LateralOffset) > maxLat)
                    {
                        RoadObstacles[i].LateralSpeed = -RoadObstacles[i].LateralSpeed;
                        RoadObstacles[i].LateralOffset = Math.Clamp(RoadObstacles[i].LateralOffset, -maxLat, maxLat);
                    }
                    int wp = RoadObstacles[i].WaypointIdx;
                    Vector2 pt = track.CenterPoints[wp];
                    Vector2 norm = track.GetNormal(wp);
                    RoadObstacles[i].X = pt.X + norm.X * RoadObstacles[i].LateralOffset;
                    RoadObstacles[i].Z = pt.Y + norm.Y * RoadObstacles[i].LateralOffset;
                }

                if (RoadObstacles[i].Y > 1.8f) continue; // Havada düşerken henüz çarpmasın

                float hitR2 = (RoadObstacles[i].Radius + 0.85f) * (RoadObstacles[i].Radius + 0.85f);
                for (int c = 0; c < cars.Count; c++)
                {
                    CarEntity car = cars[c];
                    float dx = car.X - RoadObstacles[i].X, dz = car.Z - RoadObstacles[i].Z;
                    if (dx * dx + dz * dz < hitR2)
                    {
                        // Kademeli Hasar: Her çarpmada 28 HP düşer, yere parçacıklar dökülür,
                        // önce duman, sonra alev çıkar ve en son havaya uçarak patlar!
                        car.ApplyObstacleCollision(RoadObstacles[i].X, RoadObstacles[i].Z, 28f, particles);
                    }
                }
            }

            // Dekor Binalarına Çarpışma Kontrolü
            for (int s = 0; s < track.Scenery.Count; s++)
            {
                SceneryObstacle bld = track.Scenery[s];
                if (!bld.Collidable) continue;
                float hitR2 = (bld.Radius + 0.9f) * (bld.Radius + 0.9f);
                for (int c = 0; c < cars.Count; c++)
                {
                    CarEntity car = cars[c];
                    float dx = car.X - bld.X, dz = car.Z - bld.Z;
                    if (dx * dx + dz * dz < hitR2)
                    {
                        car.ApplyObstacleCollision(bld.X, bld.Z, 32f, particles);
                    }
                }
            }
        }

        private bool IsEnemyInFrontSight(CarEntity shooter, List<CarEntity> cars)
        {
            float fx = -(float)Math.Sin(shooter.YawAngle);
            float fz = -(float)Math.Cos(shooter.YawAngle);
            for (int i = 0; i < cars.Count; i++)
            {
                CarEntity other = cars[i];
                if (other.CarId == shooter.CarId || other.IsExploded) continue;
                float dx = other.X - shooter.X, dz = other.Z - shooter.Z;
                float dSq = dx * dx + dz * dz;
                if (dSq > 1600f || dSq < 1f) continue;
                float d = (float)Math.Sqrt(dSq);
                if ((dx / d) * fx + (dz / d) * fz > 0.93f) return true;
            }
            return false;
        }

        private void FireProjectile(CarEntity owner, PowerUpType type, float speed, float fwdOffset, float life)
        {
            for (int i = 0; i < Projectiles.Length; i++)
            {
                if (!Projectiles[i].Active)
                {
                    float fx = -(float)Math.Sin(owner.YawAngle);
                    float fz = -(float)Math.Cos(owner.YawAngle);
                    Projectiles[i] = new ProjectileItem
                    {
                        Active = true,
                        OwnerId = owner.CarId,
                        Type = type,
                        X = owner.X + fx * fwdOffset,
                        Z = owner.Z + fz * fwdOffset,
                        VX = fx * speed,
                        VZ = fz * speed,
                        Life = life
                    };
                    return;
                }
            }
        }
    }

    // =========================================================================
    // ANA 3D OPENGL ES 2.0 MOTORU VE KAMERA / FİZİK DÖNGÜSÜ
    // =========================================================================
    public void OnSurfaceCreated(IGL10 gl, EGLConfig config)
    {
        private readonly Activity activity;
        private readonly Vibrator vibrator;
        private GLSurfaceView glView;
        private HudOverlayView hudView;

        public GameState State = GameState.MENU;
        public int SelectedMap = 0;
        public int SelectedColorIndex = 0;
        public bool SelectedSportModel = false;
        public bool HighQuality = true;
        public bool VibeEnabled = true;
        public int SensitivityLevel = 1; // 0: Düşük, 1: Orta, 2: Yüksek

        public readonly Vector3[] CarPalette = {
            new Vector3(0.08f, 0.27f, 0.86f), // Mavi
            new Vector3(0.83f, 0.18f, 0.18f), // Kırmızı
            new Vector3(0.98f, 0.75f, 0.18f), // Altın Sarı
            new Vector3(0.26f, 0.63f, 0.28f), // Yeşil
            new Vector3(0.98f, 0.55f, 0.0f),  // Turuncu
            new Vector3(0.56f, 0.14f, 0.67f), // Mor
            new Vector3(0.93f, 0.93f, 0.93f), // Beyaz
            new Vector3(0.0f, 0.90f, 1.0f)    // Camgöbeği
        };

        public readonly string[] MapNames = { "Classic", "Oval Valley", "Canyon", "Cyber", "Snow Peak", "Halloween" };
        public readonly int[] BestScores = new int[6];

        public TrackData CurrentTrack;
        public readonly List<CarEntity> Cars = new List<CarEntity>();
        public CarEntity PlayerCar => Cars.Count > 0 ? Cars[0] : null;
        public readonly ParticleAndDebrisSystem Particles = new ParticleAndDebrisSystem();
        public readonly WorldObjectsManager WorldObjects = new WorldObjectsManager();
        public readonly BluetoothCoopManager CoopManager;

        // Sabit fizik adımı ve zamanlayıcılar (CrownDriftGame.java)
        private const float STEP = 1f / 60f;
        private const float BOOST_SECONDS = 3.0f;
        private const float COMBO_BREAK_SECONDS = 0.3f;
        private const float COMBO_STEP_SECONDS = 3.0f;
        private const int MAX_MULTIPLIER = 4;
        private const float POINTS_PER_SECOND = 100f;
        private const float EDGE_FACTOR = 1.5f;

        private long lastFrameNano;
        private float physicsAccumulator;
        private int stepCounter;
        public float RaceTime;
        public float LapStartTime;
        public float LastLapTime;
        public int LapsSeen;
        public int CountdownValue = 3;
        private long countdownTimerMs;
        private float hapticTimer;
        private int netSyncTick;
        private volatile int pendingStartMap = -1;

        // Döner Bezel / Taç ve Dokunmatik Girdileri
        private volatile float crownAccum;
        public float TouchSteer;
        public bool BoostRequest;

        // Kamera Durumu (CrownDriftGame.updateCamera)
        private Vector3 camPos = new Vector3(0f, 15f, 8f);
        private Vector3 lookTarget = new Vector3(0f, 0f, -5f);
        private float currentFov = 67f;
        private float shakeTime;
        private int viewportWidth = 450, viewportHeight = 450;

        // Sıralama ve Yarış Sonu Durumu
        public int CurrentPlayerRank = 1;
        public bool CurrentInDriftZone;
        public bool IsNewRecord;

        // OpenGL ES 2.0 Shader ve Tamponlar (Zero-GC)
        private int shaderProgram;
        private int aPositionLoc, aColorLoc, uMvpMatrixLoc;
        private FloatBuffer unitCubePosBuf;
        private FloatBuffer unitCubeColBuf;
        private FloatBuffer trackPosBuf;
        private FloatBuffer trackColBuf;
        private int trackVertexCount;

        private readonly float[] projMatrix = new float[16];
        private readonly float[] viewMatrix = new float[16];
        private readonly float[] vpMatrix = new float[16];
        private readonly float[] modelMatrix = new float[16];
        private readonly float[] mvpMatrix = new float[16];

        public CrownDriftEngine(Activity act)
        {
            activity = act;
            vibrator = (Vibrator)act.GetSystemService(Context.VibratorService);
            CoopManager = new BluetoothCoopManager(this);
            BestScores[0] = 5343;
            BestScores[3] = 5969;
        }

        public void AttachViews(GLSurfaceView gl, HudOverlayView hud)
        {
            glView = gl;
            hudView = hud;
        }

        public void RequestUiRefresh()
        {
            activity?.RunOnUiThread(() => hudView?.Invalidate());
        }

        public void QueueStartRace(int mapIndex)
        {
            pendingStartMap = mapIndex;
            glView?.RequestRender();
            RequestUiRefresh();
        }

        // ---------------------------------------------------------------------
        // YARIŞ BAŞLATMA VE PİST / ARAÇ KURULUMU
        // ---------------------------------------------------------------------
        public void StartRace(int mapIndex, bool broadcastToClients = true)
        {
            SelectedMap = mapIndex;
            if (broadcastToClients && CoopManager.IsMultiplayerActive && CoopManager.IsHost)
            {
                CoopManager.BroadcastRaceStartCommand(mapIndex);
            }

            CurrentTrack = new TrackData(SelectedMap, HighQuality);
            CurrentTrack.ResetFinishOrder();
            BuildTrackMeshBuffers(CurrentTrack);

            Particles.Clear();
            WorldObjects.SetupForTrack(CurrentTrack);

            Cars.Clear();
            int trailSegs = HighQuality ? 320 : 140;
            float gripVal = (SelectedMap == 4) ? 0.007f : 0.012f; // Kar/Buz haritasında ICE_GRIP

            int localSlot = CoopManager.IsMultiplayerActive ? CoopManager.LocalCarId : 0;
            Vector2 startN = CurrentTrack.GetNormal(0);

            for (int i = 0; i < 4; i++)
            {
                bool isLocal = (i == localSlot);
                int colIdx = isLocal ? SelectedColorIndex : ((SelectedColorIndex + i + 1) % CarPalette.Length);
                bool sport = isLocal ? SelectedSportModel : (i % 2 == 1);

                CarEntity c = new CarEntity(i, isLocal, CarPalette[colIdx], sport, trailSegs)
                {
                    Grip = gripVal
                };

                float lateralOffset = ((i % 2 == 0) ? -1.6f : 1.6f);
                float backOffset = (i / 2) * 3.2f;
                float fwdX = -(float)Math.Sin(CurrentTrack.StartAngle);
                float fwdZ = -(float)Math.Cos(CurrentTrack.StartAngle);

                float sx = CurrentTrack.StartX + startN.X * lateralOffset - fwdX * backOffset;
                float sz = CurrentTrack.StartZ + startN.Y * lateralOffset - fwdZ * backOffset;
                c.SetPose(sx, sz, CurrentTrack.StartAngle, 0f, snap: true);
                c.ApplyInterpolated(1f);

                if (isLocal)
                    Cars.Insert(0, c);
                else
                    Cars.Add(c);
            }

            CarEntity p = PlayerCar;
            camPos = new Vector3(p.X, 15f, p.Z + 8f);
            lookTarget = new Vector3(p.X, 0f, p.Z - 5f);
            currentFov = 67f;

            RaceTime = 0f;
            LapStartTime = 0f;
            LastLapTime = 0f;
            LapsSeen = 0;
            CurrentPlayerRank = 1;
            IsNewRecord = false;
            physicsAccumulator = 0f;
            crownAccum = 0f;
            TouchSteer = 0f;
            BoostRequest = false;

            // 3 - 2 - 1 Turnuva Başlangıç Geri Sayımı
            CountdownValue = 3;
            countdownTimerMs = Java.Lang.JavaSystem.CurrentTimeMillis();
            lastFrameNano = Java.Lang.JavaSystem.NanoTime();

            SetGameState(GameState.PLAYING);
        }

        public void SetGameState(GameState newState)
        {
            State = newState;
            UpdateRenderModeForBattery();
            RequestUiRefresh();
        }

        private void UpdateRenderModeForBattery()
        {
            if (glView == null) return;
            bool needContinuous = (State == GameState.PLAYING || State == GameState.GARAGE || State == GameState.LOBBY);
            glView.RenderMode = needContinuous ? Rendermode.Continuously : Rendermode.WhenDirty;
            if (!needContinuous) glView.RequestRender();
        }

        public void HandleCrownRotation(float delta)
        {
            if (State == GameState.PLAYING && CountdownValue < 0)
            {
                crownAccum += -delta * 0.4f;
                TriggerHaptic(5);
            }
            else if (State == GameState.MENU)
            {
                SelectedMap = (SelectedMap + (delta > 0 ? 1 : -1) + 6) % 6;
                TriggerHaptic(12);
                glView?.RequestRender();
                RequestUiRefresh();
            }
        }

        public bool OnBackPressed()
        {
            if (State == GameState.PLAYING)
            {
                SetGameState(GameState.PAUSED);
                return true;
            }
            if (State != GameState.MENU)
            {
                if (State == GameState.LOBBY) CoopManager.StopLobby();
                SetGameState(GameState.MENU);
                return true;
            }
            return false;
        }

        public void OnPause()
        {
            if (State == GameState.PLAYING)
            {
                SetGameState(GameState.PAUSED);
            }
        }

        public void TriggerHaptic(int ms)
        {
            if (!VibeEnabled || vibrator == null || ms <= 0) return;
            try
            {
                if ((int)Build.VERSION.SdkInt >= 26)
                    vibrator.Vibrate(VibrationEffect.CreateOneShot(ms, VibrationEffect.DefaultAmplitude));
                else
                    vibrator.Vibrate(ms);
            }
            catch { }
        }

        // ---------------------------------------------------------------------
        // SABİT ADIMLI FİZİK DÖNGÜSÜ (60 HZ)
        // ---------------------------------------------------------------------
        private void StepPhysics(float playerSteerInput)
        {
            if (CurrentTrack == null || Cars.Count == 0) return;

            CarEntity player = PlayerCar;
            float sensFactor = SensitivityLevel == 0 ? 0.5f : (SensitivityLevel == 2 ? 1.5f : 1.0f);

            if (BoostRequest)
            {
                BoostRequest = false;
                if (player.BoostReady && !player.IsExploded)
                {
                    player.BoostReady = false;
                    player.BoostMeter = 0f;
                    player.BoostTime = BOOST_SECONDS;
                    TriggerHaptic(85);
                }
            }

            for (int i = 0; i < Cars.Count; i++)
            {
                CarEntity c = Cars[i];
                c.BeginStep();

                if (c.IsPlayer)
                {
                    c.Steer(playerSteerInput * sensFactor);
                    c.UpdatePhysics(CurrentTrack, Particles);
                }
                else if (CoopManager.IsMultiplayerActive && CoopManager.RemotePeers[c.CarId].Active)
                {
                    PeerCarState peer = CoopManager.RemotePeers[c.CarId];
                    c.X += (peer.X - c.X) * 0.45f;
                    c.Z += (peer.Z - c.Z) * 0.45f;
                    c.YawAngle = peer.Yaw;
                    c.RollAngle = peer.Roll;
                    c.VelocityX = peer.VX;
                    c.TotalRaceProgress = peer.RaceProgress;
                    if (peer.IsExploded == 1 && !c.IsExploded)
                        c.TriggerAirborneExplosion(Particles);
                    else if (peer.IsExploded == 0)
                        c.IsExploded = false;
                }
                else
                {
                    float botSteer = c.ComputeBotSteering(CurrentTrack);
                    c.Steer(botSteer);
                    c.UpdatePhysics(CurrentTrack, Particles);
                }

                // Yoldan çıkınca oyun kesinlikle bitmez! Çim (OnGrass) %40 yavaşlatır.
                CurrentTrack.UpdateCarProgressAndSurface(c, out bool inZone);
                if (c.IsPlayer) CurrentInDriftZone = inZone;

                int smokeInterval = HighQuality ? 2 : 4;
                if (!c.IsExploded && (c.IsCurrentlyDrifting || c.OnGrass) && (stepCounter % smokeInterval == 0))
                {
                    bool leftWheel = ((stepCounter / smokeInterval) % 2 == 0);
                    Particles.SpawnTireSmoke(
                        leftWheel ? c.RearLX : c.RearRX,
                        leftWheel ? c.RearLZ : c.RearRZ,
                        c.VelocityX, c.VelocityZ, c.OnGrass, SelectedMap);
                }
            }

            // Araç - Araç Çarpışma Fiziği (Çarpışınca yere parçacık dökülür!)
            for (int i = 0; i < Cars.Count; i++)
            {
                for (int j = i + 1; j < Cars.Count; j++)
                {
                    CarEntity a = Cars[i], b = Cars[j];
                    if (a.IsExploded || b.IsExploded) continue;
                    float dx = b.X - a.X, dz = b.Z - a.Z;
                    float dSq = dx * dx + dz * dz;
                    if (dSq < 1.95f && dSq > 0.0001f)
                    {
                        float d = (float)Math.Sqrt(dSq);
                        float nx = dx / d, nz = dz / d;
                        float overlap = (1.4f - d) * 0.5f;
                        a.X -= nx * overlap; a.Z -= nz * overlap;
                        b.X += nx * overlap; b.Z += nz * overlap;
                        Particles.SpawnBouncingGroundDebris((a.X + b.X) * 0.5f, 0.35f, (a.Z + b.Z) * 0.5f, 5, a.BodyColor);
                    }
                }
            }

            WorldObjects.Update(STEP, CurrentTrack, Cars, Particles);
            Particles.Update(STEP);

            // Oyuncu Çim Yavaşlaması, Kombo, Boost ve Haptik Hesabı
            if (player.OnGrass)
            {
                player.ComboTime = 0f;
                player.Multiplier = 1;
                player.ComboGap = 0f;
                shakeTime = 0.15f;
                hapticTimer -= STEP;
                if (hapticTimer <= 0f) { TriggerHaptic(12); hapticTimer = 0.12f; }
            }
            else if (player.IsCurrentlyDrifting && !player.IsExploded)
            {
                player.ComboGap = 0f;
                player.ComboTime += STEP;
                int newMult = Math.Min(MAX_MULTIPLIER, (int)(player.ComboTime / COMBO_STEP_SECONDS) + 1);
                if (newMult > player.Multiplier) TriggerHaptic(25);
                player.Multiplier = newMult;

                float pts = player.Multiplier * (POINTS_PER_SECOND / 60f)
                            * (CurrentInDriftZone ? 2.0f : 1.0f)
                            * (player.EdgeActive ? EDGE_FACTOR : 1.0f)
                            * (player.BoostTime > 0f ? 2.0f : 1.0f);
                player.SessionScore += pts;

                if (player.BoostTime <= 0f && !player.BoostReady)
                {
                    player.BoostMeter += (player.Multiplier * 0.02f + 0.035f) * STEP;
                    if (player.BoostMeter >= 1f)
                    {
                        player.BoostMeter = 1f;
                        player.BoostReady = true;
                        TriggerHaptic(60);
                    }
                }

                hapticTimer -= STEP;
                if (hapticTimer <= 0f)
                {
                    TriggerHaptic(Math.Min((int)(player.DriftAmount * 70f + 6f), 28));
                    hapticTimer = 0.16f;
                }
            }
            else
            {
                player.ComboGap += STEP;
                if (player.ComboGap > COMBO_BREAK_SECONDS)
                {
                    player.ComboTime = 0f;
                    player.Multiplier = 1;
                }
            }

            if (player.LapsCompleted > LapsSeen)
            {
                LapsSeen = player.LapsCompleted;
                LastLapTime = RaceTime - LapStartTime;
                LapStartTime = RaceTime;
            }

            // 1.1 Hatasız Sıralama Hesabı
            CurrentPlayerRank = CalculateAccurateRank(player);

            netSyncTick++;
            if (CoopManager.IsMultiplayerActive && (netSyncTick % 3 == 0))
            {
                CoopManager.BroadcastLocalCar(player, (byte)player.ActivePowerUp);
            }

            RaceTime += STEP;
            stepCounter++;

            if (player.FinishedRace)
            {
                int finalScore = (int)player.SessionScore;
                if (finalScore > BestScores[SelectedMap])
                {
                    BestScores[SelectedMap] = finalScore;
                    IsNewRecord = true;
                }
                TriggerHaptic(280);
                SetGameState(CurrentPlayerRank == 1 ? GameState.WIN : GameState.GAME_OVER);
            }
        }

        private int CalculateAccurateRank(CarEntity target)
        {
            if (target.FinishedRace && target.LockedFinishRank > 0)
                return target.LockedFinishRank;

            int rank = 1;
            for (int i = 0; i < Cars.Count; i++)
            {
                CarEntity other = Cars[i];
                if (ReferenceEquals(other, target)) continue;

                if (other.FinishedRace)
                    rank++;
                else if (other.TotalRaceProgress > target.TotalRaceProgress)
                    rank++;
            }
            return rank;
        }

        private void UpdateCamera(float delta)
        {
            CarEntity c = PlayerCar;
            if (c == null) return;

            float smooth = 1f - (float)Math.Pow(0.85, delta * 60f);
            float fx = -(float)Math.Sin(c.RenderYaw);
            float fz = -(float)Math.Cos(c.RenderYaw);
            float speed = (float)Math.Sqrt(c.VelocityX * c.VelocityX + c.VelocityZ * c.VelocityZ);

            if (speed > 0.01f && !c.IsExploded)
            {
                fx = fx * 0.55f + (c.VelocityX / speed) * 0.45f;
                fz = fz * 0.55f + (c.VelocityZ / speed) * 0.45f;
            }
            float len = Math.Max((float)Math.Sqrt(fx * fx + fz * fz), 0.001f);
            fx /= len; fz /= len;

            float zoom = (c.Multiplier - 1) * 0.6f + (c.BoostTime > 0f ? 1.2f : 0f);
            float dist = 7f + 0.6f * zoom;

            Vector3 desiredPos = new Vector3(c.RenderX - fx * dist, 15f + zoom, c.RenderZ - fz * dist);
            Vector3 desiredLook = new Vector3(c.RenderX + fx * 12f, 0f, c.RenderZ + fz * 12f);

            camPos = Vector3.Lerp(camPos, desiredPos, smooth);
            lookTarget = Vector3.Lerp(lookTarget, desiredLook, smooth);

            if (shakeTime > 0f)
            {
                shakeTime -= delta;
                camPos.X += ((float)Random.Shared.NextDouble() - 0.5f) * 0.25f;
                camPos.Y += ((float)Random.Shared.NextDouble() - 0.5f) * 0.15f;
            }

            float targetFov = c.BoostTime > 0f ? 75f : 67f;
            currentFov += (targetFov - currentFov) * smooth;
        }

        // ---------------------------------------------------------------------
        // OPENGL ES 2.0 YAŞAM DÖNGÜSÜ VE 3D SAHNE ÇİZİMİ
        // ---------------------------------------------------------------------
        public void OnSurfaceCreated(IGL10 gl, EGLConfig config)
        {
            GLES20.GlEnable(GLES20.GlDepthTest);
            GLES20.GlEnable(GLES20.GlBlend);
            GLES20.GlBlendFunc(GLES20.GlSrcAlpha, GLES20.GlOneMinusSrcAlpha);

            string vs = @"
                uniform mat4 uMVPMatrix;
                attribute vec4 aPosition;
                attribute vec4 aColor;
                varying vec4 vColor;
                void main() {
                    gl_Position = uMVPMatrix * aPosition;
                    vColor = aColor;
                }";
            string fs = @"
                precision mediump float;
                varying vec4 vColor;
                void main() {
                    gl_FragColor = vColor;
                }";

            int vShader = LoadShader(GLES20.GlVertexShader, vs);
            int fShader = LoadShader(GLES20.GlFragmentShader, fs);
            shaderProgram = GLES20.GlCreateProgram();
            GLES20.GlAttachShader(shaderProgram, vShader);
            GLES20.GlAttachShader(shaderProgram, fShader);
            GLES20.GlLinkProgram(shaderProgram);

            aPositionLoc = GLES20.GlGetAttribLocation(shaderProgram, "aPosition");
            aColorLoc = GLES20.GlGetAttribLocation(shaderProgram, "aColor");
            uMvpMatrixLoc = GLES20.GlGetUniformLocation(shaderProgram, "uMVPMatrix");

            BuildUnitCubeBuffers();
            CurrentTrack = new TrackData(SelectedMap, HighQuality);
            BuildTrackMeshBuffers(CurrentTrack);
            lastFrameNano = Java.Lang.JavaSystem.NanoTime();
        }

        public void OnSurfaceChanged(IGL10 gl, int width, int height)
        {
            viewportWidth = Math.Max(1, width);
            viewportHeight = Math.Max(1, height);
            GLES20.GlViewport(0, 0, viewportWidth, viewportHeight);
        }

        public void OnDrawFrame(IGL10 gl)
        {
            if (pendingStartMap >= 0)
            {
                int mapToStart = pendingStartMap;
                pendingStartMap = -1;
                StartRace(mapToStart, broadcastToClients: false);
            }

            long now = Java.Lang.JavaSystem.NanoTime();
            float delta = Math.Clamp((now - lastFrameNano) / 1_000_000_000f, 0.001f, 0.25f);
            lastFrameNano = now;

            if (State == GameState.PLAYING || State == GameState.PAUSED || State == GameState.WIN || State == GameState.GAME_OVER)
            {
                if (SelectedMap == 3 || SelectedMap == 5) GLES20.GlClearColor(0.03f, 0.03f, 0.07f, 1f);
                else if (SelectedMap == 4) GLES20.GlClearColor(0.75f, 0.87f, 0.95f, 1f);
                else GLES20.GlClearColor(0.14f, 0.30f, 0.12f, 1f);
            }
            else
            {
                GLES20.GlClearColor(0.05f, 0.07f, 0.10f, 1f);
            }

            GLES20.GlClear(GLES20.GlColorBufferBit | GLES20.GlDepthBufferBit);

            if (State == GameState.PLAYING)
            {
                if (CountdownValue >= 0)
                {
                    long curMs = Java.Lang.JavaSystem.CurrentTimeMillis();
                    if (curMs - countdownTimerMs > 750)
                    {
                        CountdownValue--;
                        countdownTimerMs = curMs;
                        TriggerHaptic(45);
                    }
                }
                else
                {
                    physicsAccumulator += delta;
                    int steps = (int)(physicsAccumulator / STEP);
                    if (steps > 0)
                    {
                        float crownPerStep = crownAccum / steps;
                        crownAccum = 0f;
                        for (int i = 0; i < steps; i++)
                        {
                            physicsAccumulator -= STEP;
                            StepPhysics(TouchSteer + crownPerStep);
                            if (State != GameState.PLAYING) break;
                        }
                    }
                }

                float alpha = Math.Clamp(physicsAccumulator / STEP, 0f, 1f);
                for (int i = 0; i < Cars.Count; i++) Cars[i].ApplyInterpolated(alpha);
                UpdateCamera(delta);
                RequestUiRefresh();
            }

            if (State == GameState.PLAYING || State == GameState.PAUSED || State == GameState.WIN || State == GameState.GAME_OVER)
            {
                Render3DRaceScene();
            }
            else if (State == GameState.GARAGE)
            {
                Render3DGaragePreview(now / 1_000_000_000f);
            }
        }

        private void Render3DRaceScene()
        {
            GLES20.GlUseProgram(shaderProgram);
            GLES20.GlEnableVertexAttribArray(aPositionLoc);
            GLES20.GlEnableVertexAttribArray(aColorLoc);

            float aspect = (float)viewportWidth / viewportHeight;
            Matrix.PerspectiveM(projMatrix, 0, currentFov, aspect, 1.0f, 220.0f);
            Matrix.SetLookAtM(viewMatrix, 0,
                camPos.X, camPos.Y, camPos.Z,
                lookTarget.X, lookTarget.Y, lookTarget.Z,
                0f, 1f, 0f);
            Matrix.MultiplyMM(vpMatrix, 0, projMatrix, 0, viewMatrix, 0);

            // 1. Pist, Kerbler ve Başlangıç Çizgisi
            if (trackPosBuf != null && trackVertexCount > 0)
            {
                Matrix.SetIdentityM(modelMatrix, 0);
                Matrix.MultiplyMM(mvpMatrix, 0, vpMatrix, 0, modelMatrix, 0);
                GLES20.GlUniformMatrix4fv(uMvpMatrixLoc, 1, false, mvpMatrix, 0);
                trackPosBuf.Position(0);
                GLES20.GlVertexAttribPointer(aPositionLoc, 3, GLES20.GlFloat, false, 0, trackPosBuf);
                trackColBuf.Position(0);
                GLES20.GlVertexAttribPointer(aColorLoc, 4, GLES20.GlFloat, false, 0, trackColBuf);
                GLES20.GlDrawArrays(GLES20.GlTriangles, 0, trackVertexCount);
            }

            // 2. Lastik İzleri (SkidTrail)
            Matrix.SetIdentityM(modelMatrix, 0);
            Matrix.MultiplyMM(mvpMatrix, 0, vpMatrix, 0, modelMatrix, 0);
            GLES20.GlUniformMatrix4fv(uMvpMatrixLoc, 1, false, mvpMatrix, 0);
            for (int i = 0; i < Cars.Count; i++)
            {
                Cars[i].Trail.Render(aPositionLoc, aColorLoc);
            }

            // 3. Başlangıç Kemeri ve Dekor Binaları
            if (CurrentTrack != null)
            {
                for (int i = 0; i < CurrentTrack.Scenery.Count; i++)
                {
                    SceneryObstacle b = CurrentTrack.Scenery[i];
                    DrawBox(b.X, b.Y, b.Z, b.ScaleX, b.ScaleY, b.ScaleZ, b.YawDeg, 0f, 0f, b.Color.X, b.Color.Y, b.Color.Z, 1f);
                }
            }

            // 4. Sürpriz Kutular, Sabit Bariyerler, Hareketli Dinamik Engeller ve Füzeler
            for (int i = 0; i < WorldObjects.Boxes.Length; i++)
            {
                if (!WorldObjects.Boxes[i].Active) continue;
                DrawBox(WorldObjects.Boxes[i].X, 0.7f, WorldObjects.Boxes[i].Z,
                        1.1f, 1.1f, 1.1f, WorldObjects.Boxes[i].RotAngle, 25f, 0f, 1.0f, 0.82f, 0.1f, 0.92f);
            }
            for (int i = 0; i < WorldObjects.RoadObstacles.Length; i++)
            {
                var obs = WorldObjects.RoadObstacles[i];
                if (!obs.Active) continue;
                if (obs.IsFallingDynamic || Math.Abs(obs.LateralSpeed) > 0.01f)
                    DrawBox(obs.X, obs.Y, obs.Z, 1.65f, 1.1f, 1.65f, 0f, 0f, 0f, 1.0f, 0.38f, 0.05f, 1f);
                else
                    DrawBox(obs.X, obs.Y, obs.Z, 2.1f, 1.0f, 0.7f, 0f, 0f, 0f, 0.85f, 0.15f, 0.15f, 1f);
            }
            for (int i = 0; i < WorldObjects.Projectiles.Length; i++)
            {
                var prj = WorldObjects.Projectiles[i];
                if (!prj.Active) continue;
                if (prj.Type == PowerUpType.Spike)
                    DrawBox(prj.X, 0.25f, prj.Z, 1.3f, 0.45f, 1.3f, 0f, 0f, 0f, 0.9f, 0.1f, 0.4f, 1f);
                else
                    DrawBox(prj.X, 0.55f, prj.Z, 0.45f, 0.45f, 1.2f, 0f, 0f, 0f, 1.0f, 0.25f, 0.05f, 1f);
            }

            // 5. Araçlar
            for (int i = 0; i < Cars.Count; i++)
            {
                DrawCarModel3D(Cars[i]);
            }

            // 6. Yerde Seken 3D Parçacıklar (DebrisPool) ve Motor Alevi / Duman (SmokePool)
            for (int i = 0; i < ParticleAndDebrisSystem.MAX_DEBRIS; i++)
            {
                var d = Particles.DebrisPool[i];
                if (!d.Active) continue;
                DrawBox(d.X, d.Y, d.Z, d.Size, d.Size, d.Size, d.RotAngle, d.RotAngle, 0f, d.R, d.G, d.B, 1f);
            }
            for (int i = 0; i < ParticleAndDebrisSystem.MAX_SMOKE_FIRE; i++)
            {
                var s = Particles.SmokePool[i];
                if (!s.Active) continue;
                float k = s.Life / s.MaxLife;
                float sz = s.Size + (1f - k) * 1.8f;
                DrawBox(s.X, s.Y, s.Z, sz, sz * 0.7f, sz, 0f, 0f, 0f, s.R, s.G, s.B, k * 0.52f);
            }
        }

        private void Render3DGaragePreview(float timeSec)
        {
            GLES20.GlUseProgram(shaderProgram);
            GLES20.GlEnableVertexAttribArray(aPositionLoc);
            GLES20.GlEnableVertexAttribArray(aColorLoc);

            float aspect = (float)viewportWidth / viewportHeight;
            Matrix.PerspectiveM(projMatrix, 0, 34f, aspect, 0.5f, 50f);
            Matrix.SetLookAtM(viewMatrix, 0, 0f, 3.8f, 8.5f, 0f, 0.2f, 0f, 0f, 1f, 0f);
            Matrix.MultiplyMM(vpMatrix, 0, projMatrix, 0, viewMatrix, 0);

            CarEntity preview = new CarEntity(0, true, CarPalette[SelectedColorIndex], SelectedSportModel, 4)
            {
                RenderX = 0f,
                RenderY = 0.8f,
                RenderZ = 0f,
                RenderYaw = timeSec * 0.9f
            };
            DrawCarModel3D(preview);
        }

        private void DrawCarModel3D(CarEntity c)
        {
            float yawDeg = (float)(c.RenderYaw * 180.0 / Math.PI);
            // Hasar aldıkça rengi hafif kararır, patladığında kömürleşir
            float dmgTint = c.IsExploded ? 0.22f : (0.55f + 0.45f * (c.Health / 100f));
            Vector3 col = c.BodyColor * dmgTint;
            Vector3 dark = col * 0.55f;

            // Araç Altı Gölge
            DrawBox(c.RenderX, 0.03f, c.RenderZ, 1.55f, 0.02f, 2.85f, yawDeg, 0f, 0f, 0f, 0f, 0f, 0.48f);

            // Far Işığı Huzmesi
            if (!c.IsExploded && (SelectedMap == 3 || SelectedMap == 5))
            {
                DrawPartOnCar(c, 0f, -0.15f, -4.2f, 2.4f, 0.02f, 5.5f, 0f, 1.0f, 0.95f, 0.75f, 0.22f);
            }

            float chW = c.IsSportModel ? 1.38f : 1.24f;
            float chH = c.IsSportModel ? 0.22f : 0.28f;
            float chD = c.IsSportModel ? 2.72f : 2.50f;
            DrawPartOnCar(c, 0f, 0f, 0f, chW, chH, chD, 0f, col.X, col.Y, col.Z, 1f);
            DrawPartOnCar(c, 0f, -0.16f, 0f, chW + 0.06f, 0.08f, chD - 0.2f, 0f, dark.X, dark.Y, dark.Z, 1f);

            DrawPartOnCar(c, 0f, 0.26f, 0.15f, 0.92f, 0.26f, 1.10f, 0f, 0.53f, 0.80f, 1.0f, 1f);
            DrawPartOnCar(c, 0f, 0.41f, 0.15f, 0.86f, 0.04f, 0.90f, 0f, col.X, col.Y, col.Z, 1f);

            if (c.IsSportModel && !c.IsExploded)
            {
                DrawPartOnCar(c, 0f, 0.13f, -0.72f, 0.26f, 0.02f, 1.0f, 0f, 0.96f, 0.96f, 0.98f, 1f);
                DrawPartOnCar(c, 0f, 0.44f, 0.15f, 0.26f, 0.02f, 0.85f, 0f, 0.96f, 0.96f, 0.98f, 1f);
            }

            DrawPartOnCar(c, 0f, 0.44f, 1.14f, c.IsSportModel ? 1.44f : 1.20f, 0.05f, 0.28f, 0f, dark.X, dark.Y, dark.Z, 1f);
            DrawPartOnCar(c, 0.38f, 0.28f, 1.12f, 0.06f, 0.26f, 0.12f, 0f, dark.X, dark.Y, dark.Z, 1f);
            DrawPartOnCar(c, -0.38f, 0.28f, 1.12f, 0.06f, 0.26f, 0.12f, 0f, dark.X, dark.Y, dark.Z, 1f);

            DrawPartOnCar(c, 0.40f, 0.02f, -chD * 0.5f, 0.30f, 0.09f, 0.05f, 0f, 1.0f, 0.96f, 0.83f, 1f);
            DrawPartOnCar(c, -0.40f, 0.02f, -chD * 0.5f, 0.30f, 0.09f, 0.05f, 0f, 1.0f, 0.96f, 0.83f, 1f);
            DrawPartOnCar(c, 0.40f, 0.04f, chD * 0.5f, 0.28f, 0.08f, 0.05f, 0f, 1.0f, 0.16f, 0.16f, 1f);
            DrawPartOnCar(c, -0.40f, 0.04f, chD * 0.5f, 0.28f, 0.08f, 0.05f, 0f, 1.0f, 0.16f, 0.16f, 1f);

            // Ön Tekerlekler (±28° döner) ve Arka Tekerlekler
            DrawPartOnCar(c, 0.68f, -0.10f, -0.82f, 0.27f, 0.42f, 0.45f, c.FrontWheelAngleDeg, 0.12f, 0.12f, 0.12f, 1f);
            DrawPartOnCar(c, -0.68f, -0.10f, -0.82f, 0.27f, 0.42f, 0.45f, c.FrontWheelAngleDeg, 0.12f, 0.12f, 0.12f, 1f);
            DrawPartOnCar(c, 0.68f, -0.10f, 0.82f, 0.30f, 0.42f, 0.48f, 0f, 0.12f, 0.12f, 0.12f, 1f);
            DrawPartOnCar(c, -0.68f, -0.10f, 0.82f, 0.30f, 0.42f, 0.48f, 0f, 0.12f, 0.12f, 0.12f, 1f);

            if (c.ActivePowerUp == PowerUpType.FrontWeapon)
            {
                DrawPartOnCar(c, 0f, 0.15f, -1.45f, 0.35f, 0.22f, 0.75f, 0f, 1.0f, 0.4f, 0.0f, 1f);
            }
            if (c.ActivePowerUp == PowerUpType.Shield)
            {
                DrawPartOnCar(c, 0f, 0.15f, 0f, 1.85f, 1.05f, 3.1f, 0f, 0.0f, 0.88f, 1.0f, 0.28f);
            }
        }

        private void DrawPartOnCar(CarEntity c, float lx, float ly, float lz, float sx, float sy, float sz, float localYaw, float r, float g, float b, float a)
        {
            float yawDeg = (float)(c.RenderYaw * 180.0 / Math.PI);
            Matrix.SetIdentityM(modelMatrix, 0);
            Matrix.TranslateM(modelMatrix, 0, c.RenderX, c.RenderY, c.RenderZ);
            Matrix.RotateM(modelMatrix, 0, yawDeg, 0f, 1f, 0f);
            Matrix.RotateM(modelMatrix, 0, c.RenderRoll, 0f, 0f, 1f);
            Matrix.RotateM(modelMatrix, 0, c.RenderPitch, 1f, 0f, 0f);
            Matrix.TranslateM(modelMatrix, 0, lx, ly, lz);
            if (Math.Abs(localYaw) > 0.01f)
                Matrix.RotateM(modelMatrix, 0, localYaw, 0f, 1f, 0f);
            Matrix.ScaleM(modelMatrix, 0, sx, sy, sz);

            Matrix.MultiplyMM(mvpMatrix, 0, vpMatrix, 0, modelMatrix, 0);
            GLES20.GlUniformMatrix4fv(uMvpMatrixLoc, 1, false, mvpMatrix, 0);
            DrawUnitCubeColor(r, g, b, a);
        }

        private void DrawBox(float x, float y, float z, float sx, float sy, float sz, float yaw, float pitch, float roll, float r, float g, float b, float a)
        {
            Matrix.SetIdentityM(modelMatrix, 0);
            Matrix.TranslateM(modelMatrix, 0, x, y, z);
            if (Math.Abs(yaw) > 0.01f) Matrix.RotateM(modelMatrix, 0, yaw, 0f, 1f, 0f);
            if (Math.Abs(pitch) > 0.01f) Matrix.RotateM(modelMatrix, 0, pitch, 1f, 0f, 0f);
            if (Math.Abs(roll) > 0.01f) Matrix.RotateM(modelMatrix, 0, roll, 0f, 0f, 1f);
            Matrix.ScaleM(modelMatrix, 0, sx, sy, sz);

            Matrix.MultiplyMM(mvpMatrix, 0, vpMatrix, 0, modelMatrix, 0);
            GLES20.GlUniformMatrix4fv(uMvpMatrixLoc, 1, false, mvpMatrix, 0);
            DrawUnitCubeColor(r, g, b, a);
        }

        private readonly float[] cubeColorsTemp = new float[36 * 4];
        private void DrawUnitCubeColor(float r, float g, float b, float a)
        {
            for (int i = 0; i < 36; i++)
            {
                float shade = (i < 12) ? 0.88f : (i < 24 ? 1.0f : 0.74f);
                int o = i * 4;
                cubeColorsTemp[o] = r * shade;
                cubeColorsTemp[o + 1] = g * shade;
                cubeColorsTemp[o + 2] = b * shade;
                cubeColorsTemp[o + 3] = a;
            }
            unitCubeColBuf.Position(0);
            unitCubeColBuf.Put(cubeColorsTemp);
            unitCubeColBuf.Position(0);

            unitCubePosBuf.Position(0);
            GLES20.GlVertexAttribPointer(aPositionLoc, 3, GLES20.GlFloat, false, 0, unitCubePosBuf);
            GLES20.GlVertexAttribPointer(aColorLoc, 4, GLES20.GlFloat, false, 0, unitCubeColBuf);
            GLES20.GlDrawArrays(GLES20.GlTriangles, 0, 36);
        }

        private void BuildUnitCubeBuffers()
        {
            float[] v = {
                -0.5f,-0.5f, 0.5f,  0.5f,-0.5f, 0.5f,  0.5f, 0.5f, 0.5f, -0.5f,-0.5f, 0.5f,  0.5f, 0.5f, 0.5f, -0.5f, 0.5f, 0.5f,
                -0.5f,-0.5f,-0.5f, -0.5f, 0.5f,-0.5f,  0.5f, 0.5f,-0.5f, -0.5f,-0.5f,-0.5f,  0.5f, 0.5f,-0.5f,  0.5f,-0.5f,-0.5f,
                -0.5f, 0.5f,-0.5f, -0.5f, 0.5f, 0.5f,  0.5f, 0.5f, 0.5f, -0.5f, 0.5f,-0.5f,  0.5f, 0.5f, 0.5f,  0.5f, 0.5f,-0.5f,
                -0.5f,-0.5f,-0.5f,  0.5f,-0.5f,-0.5f,  0.5f,-0.5f, 0.5f, -0.5f,-0.5f,-0.5f,  0.5f,-0.5f, 0.5f, -0.5f,-0.5f, 0.5f,
                -0.5f,-0.5f,-0.5f, -0.5f,-0.5f, 0.5f, -0.5f, 0.5f, 0.5f, -0.5f,-0.5f,-0.5f, -0.5f, 0.5f, 0.5f, -0.5f, 0.5f,-0.5f,
                 0.5f,-0.5f,-0.5f,  0.5f, 0.5f,-0.5f,  0.5f, 0.5f, 0.5f,  0.5f,-0.5f,-0.5f,  0.5f, 0.5f, 0.5f,  0.5f,-0.5f, 0.5f
            };
            ByteBuffer bb = ByteBuffer.AllocateDirect(v.Length * 4);
            bb.Order(ByteOrder.NativeOrder());
            unitCubePosBuf = bb.AsFloatBuffer();
            unitCubePosBuf.Put(v);
            unitCubePosBuf.Position(0);

            ByteBuffer cb = ByteBuffer.AllocateDirect(36 * 4 * 4);
            cb.Order(ByteOrder.NativeOrder());
            unitCubeColBuf = cb.AsFloatBuffer();
        }

        private void BuildTrackMeshBuffers(TrackData t)
        {
            int n = t.CenterPoints.Count;
            if (n < 3) return;

            List<float> posList = new List<float>(n * 54);
            List<float> colList = new List<float>(n * 72);

            float tw = t.TrackWidth;
            float cw = 0.85f;
            Vector3 roadCol = t.MapType == 3 ? new Vector3(0.21f, 0.21f, 0.28f)
                            : t.MapType == 4 ? new Vector3(0.43f, 0.53f, 0.59f)
                            : new Vector3(0.26f, 0.27f, 0.29f);

            for (int i = 0; i < n; i++)
            {
                int next = (i + 1) % n;
                Vector2 p = t.CenterPoints[i];
                Vector2 q = t.CenterPoints[next];
                Vector2 np = t.GetNormal(i);
                Vector2 nq = t.GetNormal(next);

                AddQuad(posList, colList,
                    p.X + np.X * tw, 0f, p.Y + np.Y * tw,
                    q.X + nq.X * tw, 0f, q.Y + nq.Y * tw,
                    q.X - nq.X * tw, 0f, q.Y - nq.Y * tw,
                    p.X - np.X * tw, 0f, p.Y - np.Y * tw,
                    roadCol.X, roadCol.Y, roadCol.Z, 1f);

                bool isZone = t.ZoneMask.Length > i && t.ZoneMask[i];
                bool alt = (i % 2 == 0);
                Vector3 curbCol;
                if (isZone)
                    curbCol = alt ? new Vector3(1.0f, 0.76f, 0.03f) : new Vector3(0.1f, 0.1f, 0.1f);
                else
                    curbCol = alt ? new Vector3(0.85f, 0.18f, 0.18f) : new Vector3(0.95f, 0.95f, 0.95f);

                AddQuad(posList, colList,
                    p.X + np.X * (tw + cw), 0.03f, p.Y + np.Y * (tw + cw),
                    q.X + nq.X * (tw + cw), 0.03f, q.Y + nq.Y * (tw + cw),
                    q.X + nq.X * tw, 0.03f, q.Y + nq.Y * tw,
                    p.X + np.X * tw, 0.03f, p.Y + np.Y * tw,
                    curbCol.X, curbCol.Y, curbCol.Z, 1f);

                AddQuad(posList, colList,
                    p.X - np.X * tw, 0.03f, p.Y - np.Y * tw,
                    q.X - nq.X * tw, 0.03f, q.Y - nq.Y * tw,
                    q.X - nq.X * (tw + cw), 0.03f, q.Y - nq.Y * (tw + cw),
                    p.X - np.X * (tw + cw), 0.03f, p.Y - np.Y * (tw + cw),
                    curbCol.X, curbCol.Y, curbCol.Z, 1f);

                if (i == 0)
                {
                    AddQuad(posList, colList,
                        p.X + np.X * tw, 0.04f, p.Y + np.Y * tw,
                        q.X + nq.X * tw, 0.04f, q.Y + nq.Y * tw,
                        q.X - nq.X * tw, 0.04f, q.Y - nq.Y * tw,
                        p.X - np.X * tw, 0.04f, p.Y - np.Y * tw,
                        1f, 1f, 1f, 1f);
                }
            }

            trackVertexCount = posList.Count / 3;
            ByteBuffer pb = ByteBuffer.AllocateDirect(posList.Count * 4);
            pb.Order(ByteOrder.NativeOrder());
            trackPosBuf = pb.AsFloatBuffer();
            trackPosBuf.Put(posList.ToArray());
            trackPosBuf.Position(0);

            ByteBuffer cb = ByteBuffer.AllocateDirect(colList.Count * 4);
            cb.Order(ByteOrder.NativeOrder());
            trackColBuf = cb.AsFloatBuffer();
            trackColBuf.Put(colList.ToArray());
            trackColBuf.Position(0);
        }

        private static void AddQuad(List<float> pos, List<float> col,
            float x1, float y1, float z1, float x2, float y2, float z2,
            float x3, float y3, float z3, float x4, float y4, float z4,
            float r, float g, float b, float a)
        {
            float[] v = { x1, y1, z1, x2, y2, z2, x3, y3, z3, x1, y1, z1, x3, y3, z3, x4, y4, z4 };
            pos.AddRange(v);
            for (int i = 0; i < 6; i++) { col.Add(r); col.Add(g); col.Add(b); col.Add(a); }
        }

        private static int LoadShader(int type, string code)
        {
            int s = GLES20.GlCreateShader(type);
            GLES20.GlShaderSource(s, code);
            GLES20.GlCompileShader(s);
            return s;
        }

        protected override void Dispose(bool disposing)
        {
            CoopManager?.Dispose();
            base.Dispose(disposing);
        }
    }

    // =========================================================================
    // 2. YENİ ARAYÜZ (UI): SOL ÜSTTE KÜÇÜK SIRALAMA ("2/4"), HARİTA ÜSTÜ PAUSE,
    //    SOLDA TURBO İKONU VE ALTINDA AKTİF ÖZELLİK SİMGESİ
    // =========================================================================
    public class HudOverlayView : View
    {
        private readonly CrownDriftEngine engine;
        private readonly Paint bgPaint = new Paint(PaintFlags.AntiAlias);
        private readonly Paint accentPaint = new Paint(PaintFlags.AntiAlias);
        private readonly Paint textPaint = new Paint(PaintFlags.AntiAlias);
        private readonly Paint smallTextPaint = new Paint(PaintFlags.AntiAlias);
        private readonly Paint mapLinePaint = new Paint(PaintFlags.AntiAlias);

        private readonly RectF mainBtnRect = new RectF();
        private readonly RectF coopBtnRect = new RectF();
        private readonly RectF prevMapRect = new RectF();
        private readonly RectF nextMapRect = new RectF();
        private readonly RectF garageBtnRect = new RectF();
        private readonly RectF settingsBtnRect = new RectF();
        private readonly RectF pauseBtnRect = new RectF();
        private readonly RectF turboBtnRect = new RectF();

        private int lastScoreKey = -1;
        private string cachedScoreStr = "0";
        private int lastRankKey = -1;
        private string cachedRankStr = "1/4";

        public HudOverlayView(Context ctx, CrownDriftEngine eng) : base(ctx)
        {
            engine = eng;
            textPaint.Color = Color.White;
            textPaint.TextAlign = Paint.Align.Center;
            textPaint.SetTypeface(Typeface.DefaultBold);

            smallTextPaint.Color = Color.Argb(230, 200, 215, 230);
            smallTextPaint.TextAlign = Paint.Align.Center;
            smallTextPaint.SetTypeface(Typeface.DefaultBold);

            mapLinePaint.SetStyle(Paint.Style.Stroke);
            mapLinePaint.StrokeCap = Paint.Cap.Round;
        }

        protected override void OnDraw(Canvas canvas)
        {
            base.OnDraw(canvas);
            float w = Width, h = Height;
            float cx = w * 0.5f, cy = h * 0.5f;
            float scale = w / 450f;

            textPaint.TextSize = 32f * scale;
            smallTextPaint.TextSize = 20f * scale;

            switch (engine.State)
            {
                case GameState.MENU:
                    DrawMenuScreen(canvas, w, h, cx, cy, scale);
                    break;
                case GameState.LOBBY:
                    DrawBluetoothLobbyScreen(canvas, w, h, cx, cy, scale);
                    break;
                case GameState.PLAYING:
                    DrawRaceHud(canvas, w, h, cx, cy, scale);
                    break;
                case GameState.PAUSED:
                    DrawRaceHud(canvas, w, h, cx, cy, scale);
                    DrawPauseModal(canvas, w, h, cx, cy, scale);
                    break;
                case GameState.WIN:
                case GameState.GAME_OVER:
                    DrawResultScreen(canvas, w, h, cx, cy, scale);
                    break;
                case GameState.GARAGE:
                    DrawGarageScreen(canvas, w, h, cx, cy, scale);
                    break;
                case GameState.SETTINGS:
                    DrawSettingsScreen(canvas, w, h, cx, cy, scale);
                    break;
            }
        }

        private void DrawRaceHud(Canvas canvas, float w, float h, float cx, float cy, float s)
        {
            CarEntity p = engine.PlayerCar;
            if (p == null) return;

            // 1. ÜST MERKEZ: Tur ve Puan Kutusu
            int lap = Math.Min(p.LapsCompleted + 1, engine.CurrentTrack.TargetLaps);
            int scoreInt = (int)p.SessionScore;
            if (scoreInt != lastScoreKey) { lastScoreKey = scoreInt; cachedScoreStr = scoreInt.ToString(); }

            bgPaint.Color = Color.Argb(180, 12, 16, 24);
            RectF topBox = new RectF(cx - 88f * s, 18f * s, cx + 88f * s, 88f * s);
            canvas.DrawRoundRect(topBox, 20f * s, 20f * s, bgPaint);

            smallTextPaint.Color = Color.Argb(220, 180, 195, 210);
            canvas.DrawText($"TUR {lap}/{engine.CurrentTrack.TargetLaps}", cx, 42f * s, smallTextPaint);

            textPaint.Color = engine.CurrentInDriftZone ? Color.Gold : (p.IsCurrentlyDrifting ? Color.Cyan : Color.White);
            canvas.DrawText(cachedScoreStr, cx, 76f * s, textPaint);

            if (p.Multiplier > 1)
            {
                accentPaint.Color = Color.Argb(255, 236, 64, 122);
                RectF multPill = new RectF(cx + 52f * s, 52f * s, cx + 92f * s, 78f * s);
                canvas.DrawRoundRect(multPill, 12f * s, 12f * s, accentPaint);
                smallTextPaint.Color = Color.White;
                canvas.DrawText($"x{p.Multiplier}", multPill.CenterX(), multPill.CenterY() + 7f * s, smallTextPaint);
            }

            // 2. SOL ÜST: Büyük drift kutusu tamamen kaldırıldı! Sadece küçük Sıralama Göstergesi ("1/4") var.
            if (engine.CurrentPlayerRank != lastRankKey)
            {
                lastRankKey = engine.CurrentPlayerRank;
                cachedRankStr = $"{engine.CurrentPlayerRank}/4";
            }
            RectF rankRect = new RectF(36f * s, 58f * s, 102f * s, 94f * s);
            bgPaint.Color = Color.Argb(200, 18, 26, 38);
            canvas.DrawRoundRect(rankRect, 18f * s, 18f * s, bgPaint);
            smallTextPaint.Color = engine.CurrentPlayerRank == 1 ? Color.Gold : Color.Cyan;
            canvas.DrawText(cachedRankStr, rankRect.CenterX(), rankRect.CenterY() + 7f * s, smallTextPaint);

            // 3. SAĞ KENAR: Harita Balonu ve Tam Üstünde DUR (PAUSE) Butonu
            float mapBubbleCx = w - 64f * s;
            float mapBubbleCy = cy + 18f * s;
            float mapBubbleR = 40f * s;

            float pauseCx = mapBubbleCx;
            float pauseCy = mapBubbleCy - mapBubbleR - 28f * s;
            pauseBtnRect.Set(pauseCx - 24f * s, pauseCy - 24f * s, pauseCx + 24f * s, pauseCy + 24f * s);
            bgPaint.Color = Color.Argb(210, 26, 34, 48);
            canvas.DrawCircle(pauseCx, pauseCy, 22f * s, bgPaint);
            accentPaint.Color = Color.White;
            canvas.DrawRect(pauseCx - 7f * s, pauseCy - 8f * s, pauseCx - 2f * s, pauseCy + 8f * s, accentPaint);
            canvas.DrawRect(pauseCx + 2f * s, pauseCy - 8f * s, pauseCx + 7f * s, pauseCy + 8f * s, accentPaint);

            bgPaint.Color = Color.Argb(185, 12, 18, 28);
            canvas.DrawCircle(mapBubbleCx, mapBubbleCy, mapBubbleR, bgPaint);
            DrawMiniMapInsideBubble(canvas, mapBubbleCx, mapBubbleCy, mapBubbleR * 0.75f, s);

            // 4. SOL KENAR: Küçük Turbo İkonu ve Hemen Altında Aktif Özellik Simgesi
            float turboCx = 58f * s;
            float turboCy = cy - 6f * s;
            float turboR = 26f * s;
            turboBtnRect.Set(turboCx - turboR, turboCy - turboR, turboCx + turboR, turboCy + turboR);

            bgPaint.Color = Color.Argb(210, 18, 26, 38);
            canvas.DrawCircle(turboCx, turboCy, turboR, bgPaint);

            accentPaint.Color = p.BoostReady ? Color.Cyan : (p.BoostTime > 0f ? Color.Gold : Color.Argb(180, 0, 180, 220));
            canvas.DrawCircle(turboCx, turboCy, turboR * Math.Clamp(p.BoostReady || p.BoostTime > 0f ? 1f : p.BoostMeter, 0.15f, 0.88f), accentPaint);
            smallTextPaint.Color = Color.White;
            canvas.DrawText(p.BoostReady ? "TURBO" : $"{(int)(p.BoostMeter * 100)}%", turboCx, turboCy + 6f * s, smallTextPaint);

            if (p.ActivePowerUp != PowerUpType.None)
            {
                float pwCx = turboCx;
                float pwCy = turboCy + turboR + 32f * s;
                bgPaint.Color = Color.Argb(225, 32, 22, 48);
                canvas.DrawCircle(pwCx, pwCy, 22f * s, bgPaint);

                string pwIcon = p.ActivePowerUp switch
                {
                    PowerUpType.Missile => "FÜZE",
                    PowerUpType.Spike => "DİKEN",
                    PowerUpType.Shield => "KALKAN",
                    PowerUpType.FrontWeapon => "SİLAH",
                    PowerUpType.BadBoxSlow => "YAVAŞ",
                    _ => ""
                };
                smallTextPaint.Color = p.ActivePowerUp == PowerUpType.BadBoxSlow ? Color.Red : Color.Gold;
                smallTextPaint.TextSize = 14f * s;
                canvas.DrawText(pwIcon, pwCx, pwCy + 5f * s, smallTextPaint);
                smallTextPaint.TextSize = 20f * s;
            }

            // 5. ALT YAY: Referans 21 Noktalı Boost Göstergesi
            float arcRadius = Math.Min(w, h) * 0.5f - 12f * s;
            float fill = (p.BoostTime > 0f || p.BoostReady) ? 1f : p.BoostMeter;
            for (int i = 0; i < 21; i++)
            {
                double ang = (i * 2.8 - 118.0 + 180.0) * Math.PI / 180.0;
                bool on = i < (int)(fill * 21f) || p.BoostTime > 0f || p.BoostReady;
                accentPaint.Color = on ? Color.Cyan : Color.Argb(110, 40, 55, 70);
                float dx = cx + (float)Math.Cos(ang) * arcRadius;
                float dy = cy + (float)Math.Sin(ang) * arcRadius;
                canvas.DrawCircle(dx, dy, 3.4f * s, accentPaint);
            }

            // 6. Patlama Sonrası 3 Saniyelik Canlanma Geri Sayımı veya 3-2-1 Başlangıç Sayacı
            if (p.IsExploded)
            {
                bgPaint.Color = Color.Argb(150, 0, 0, 0);
                canvas.DrawRect(0, 0, w, h, bgPaint);
                textPaint.Color = Color.OrangeRed;
                canvas.DrawText($"CANLANIYOR: {Math.Ceiling(p.RespawnTimer)}s", cx, cy, textPaint);
            }
            else if (engine.CountdownValue >= 0)
            {
                bgPaint.Color = Color.Argb(150, 8, 12, 18);
                canvas.DrawCircle(cx, cy, 56f * s, bgPaint);
                textPaint.Color = engine.CountdownValue > 0 ? Color.Cyan : Color.Lime;
                textPaint.TextSize = 48f * s;
                canvas.DrawText(engine.CountdownValue > 0 ? engine.CountdownValue.ToString() : "BAŞLA!", cx, cy + 16f * s, textPaint);
            }
        }

        private void DrawMiniMapInsideBubble(Canvas canvas, float bx, float by, float maxR, float s)
        {
            TrackData t = engine.CurrentTrack;
            if (t == null || t.CenterPoints.Count == 0) return;

            float maxCoord = 1f;
            for (int i = 0; i < t.CenterPoints.Count; i++)
            {
                maxCoord = Math.Max(maxCoord, Math.Max(Math.Abs(t.CenterPoints[i].X), Math.Abs(t.CenterPoints[i].Y)));
            }
            float mapScale = maxR / maxCoord;

            mapLinePaint.StrokeWidth = 3.2f * s;
            mapLinePaint.Color = Color.Argb(180, 120, 160, 190);
            for (int i = 0; i < t.CenterPoints.Count; i += 2)
            {
                Vector2 p0 = t.CenterPoints[i];
                Vector2 p1 = t.CenterPoints[(i + 2) % t.CenterPoints.Count];
                canvas.DrawLine(bx + p0.X * mapScale, by + p0.Y * mapScale, bx + p1.X * mapScale, by + p1.Y * mapScale, mapLinePaint);
            }

            for (int i = 0; i < engine.Cars.Count; i++)
            {
                CarEntity c = engine.Cars[i];
                accentPaint.Color = c.IsPlayer ? Color.Cyan : Color.Red;
                canvas.DrawCircle(bx + c.X * mapScale, by + c.Z * mapScale, (c.IsPlayer ? 4.2f : 3.0f) * s, accentPaint);
            }
        }

        private void DrawMenuScreen(Canvas canvas, float w, float h, float cx, float cy, float s)
        {
            smallTextPaint.Color = Color.Argb(200, 150, 175, 200);
            canvas.DrawText("CROWN DRIFT WATCH", cx, 62f * s, smallTextPaint);

            textPaint.Color = Color.White;
            canvas.DrawText(engine.MapNames[engine.SelectedMap], cx, cy - 28f * s, textPaint);

            smallTextPaint.Color = Color.Gold;
            canvas.DrawText($"REKOR: {engine.BestScores[engine.SelectedMap]}", cx, cy + 8f * s, smallTextPaint);

            prevMapRect.Set(24f * s, cy - 40f * s, 84f * s, cy + 20f * s);
            nextMapRect.Set(w - 84f * s, cy - 40f * s, w - 24f * s, cy + 20f * s);
            textPaint.Color = Color.Cyan;
            canvas.DrawText("<", prevMapRect.CenterX(), prevMapRect.CenterY() + 10f * s, textPaint);
            canvas.DrawText(">", nextMapRect.CenterX(), nextMapRect.CenterY() + 10f * s, textPaint);

            garageBtnRect.Set(42f * s, cy + 25f * s, 106f * s, cy + 85f * s);
            settingsBtnRect.Set(w - 106f * s, cy + 25f * s, w - 42f * s, cy + 85f * s);
            bgPaint.Color = Color.Argb(220, 28, 38, 52);
            canvas.DrawCircle(garageBtnRect.CenterX(), garageBtnRect.CenterY(), 28f * s, bgPaint);
            canvas.DrawCircle(settingsBtnRect.CenterX(), settingsBtnRect.CenterY(), 28f * s, bgPaint);
            smallTextPaint.Color = Color.White;
            canvas.DrawText("GARAJ", garageBtnRect.CenterX(), garageBtnRect.CenterY() + 6f * s, smallTextPaint);
            canvas.DrawText("AYAR", settingsBtnRect.CenterX(), settingsBtnRect.CenterY() + 6f * s, smallTextPaint);

            mainBtnRect.Set(cx - 95f * s, h - 132f * s, cx + 95f * s, h - 78f * s);
            accentPaint.Color = Color.Argb(255, 0, 215, 255);
            canvas.DrawRoundRect(mainBtnRect, 27f * s, 27f * s, accentPaint);
            textPaint.Color = Color.Argb(255, 10, 20, 30);
            canvas.DrawText("YARIŞA BAŞLA", cx, mainBtnRect.CenterY() + 10f * s, textPaint);

            coopBtnRect.Set(cx - 85f * s, h - 68f * s, cx + 85f * s, h - 26f * s);
            bgPaint.Color = Color.Argb(230, 36, 52, 72);
            canvas.DrawRoundRect(coopBtnRect, 20f * s, 20f * s, bgPaint);
            smallTextPaint.Color = Color.Cyan;
            canvas.DrawText("BT CO-OP LOBİ (4P)", cx, coopBtnRect.CenterY() + 7f * s, smallTextPaint);
        }

        private void DrawBluetoothLobbyScreen(Canvas canvas, float w, float h, float cx, float cy, float s)
        {
            textPaint.Color = Color.Cyan;
            canvas.DrawText("4 KİŞİLİK BT LOBİ", cx, 80f * s, textPaint);

            smallTextPaint.Color = Color.White;
            canvas.DrawText(engine.CoopManager.StatusText, cx, 125f * s, smallTextPaint);
            canvas.DrawText($"Bağlı Oyuncu: {engine.CoopManager.ConnectedCount}/4", cx, 160f * s, smallTextPaint);

            prevMapRect.Set(cx - 145f * s, cy - 30f * s, cx - 8f * s, cy + 24f * s);
            nextMapRect.Set(cx + 8f * s, cy - 30f * s, cx + 145f * s, cy + 24f * s);

            bgPaint.Color = Color.Argb(230, 25, 95, 65);
            canvas.DrawRoundRect(prevMapRect, 18f * s, 18f * s, bgPaint);
            bgPaint.Color = Color.Argb(230, 30, 75, 130);
            canvas.DrawRoundRect(nextMapRect, 18f * s, 18f * s, bgPaint);

            smallTextPaint.Color = Color.White;
            canvas.DrawText("LOBİ KUR", prevMapRect.CenterX(), prevMapRect.CenterY() + 6f * s, smallTextPaint);
            canvas.DrawText("OTO KATIL", nextMapRect.CenterX(), nextMapRect.CenterY() + 6f * s, smallTextPaint);

            mainBtnRect.Set(cx - 95f * s, h - 120f * s, cx + 95f * s, h - 66f * s);
            accentPaint.Color = Color.Argb(255, 0, 215, 255);
            canvas.DrawRoundRect(mainBtnRect, 26f * s, 26f * s, accentPaint);
            textPaint.Color = Color.Black;
            canvas.DrawText("BAŞLAT", cx, mainBtnRect.CenterY() + 10f * s, textPaint);
        }

        private void DrawPauseModal(Canvas canvas, float w, float h, float cx, float cy, float s)
        {
            bgPaint.Color = Color.Argb(200, 8, 12, 18);
            canvas.DrawRect(0, 0, w, h, bgPaint);

            textPaint.Color = Color.White;
            canvas.DrawText("DURAKLATILDI", cx, cy - 50f * s, textPaint);

            mainBtnRect.Set(cx - 90f * s, cy - 15f * s, cx + 90f * s, cy + 38f * s);
            accentPaint.Color = Color.Cyan;
            canvas.DrawRoundRect(mainBtnRect, 24f * s, 24f * s, accentPaint);
            textPaint.Color = Color.Black;
            canvas.DrawText("DEVAM ET", cx, mainBtnRect.CenterY() + 10f * s, textPaint);

            coopBtnRect.Set(cx - 80f * s, cy + 52f * s, cx + 80f * s, cy + 98f * s);
            bgPaint.Color = Color.Argb(220, 45, 55, 70);
            canvas.DrawRoundRect(coopBtnRect, 20f * s, 20f * s, bgPaint);
            smallTextPaint.Color = Color.White;
            canvas.DrawText("ANA MENÜ", cx, coopBtnRect.CenterY() + 7f * s, smallTextPaint);
        }

        private void DrawResultScreen(Canvas canvas, float w, float h, float cx, float cy, float s)
        {
            bgPaint.Color = Color.Argb(205, 8, 12, 18);
            canvas.DrawRect(0, 0, w, h, bgPaint);

            bool won = (engine.CurrentPlayerRank == 1);
            textPaint.Color = won ? Color.Lime : Color.OrangeRed;
            canvas.DrawText(won ? "1. BİTİRDİN!" : $"SIRA: {engine.CurrentPlayerRank}/4", cx, 95f * s, textPaint);

            if (engine.IsNewRecord)
            {
                smallTextPaint.Color = Color.Gold;
                canvas.DrawText("YENİ REKOR!", cx, 132f * s, smallTextPaint);
            }

            textPaint.Color = Color.White;
            textPaint.TextSize = 48f * s;
            canvas.DrawText(((int)(engine.PlayerCar?.SessionScore ?? 0)).ToString(), cx, cy + 5f * s, textPaint);
            textPaint.TextSize = 32f * s;

            mainBtnRect.Set(cx - 95f * s, h - 135f * s, cx + 95f * s, h - 82f * s);
            accentPaint.Color = Color.Cyan;
            canvas.DrawRoundRect(mainBtnRect, 26f * s, 26f * s, accentPaint);
            textPaint.Color = Color.Black;
            canvas.DrawText("TEKRAR", cx, mainBtnRect.CenterY() + 10f * s, textPaint);

            coopBtnRect.Set(cx - 75f * s, h - 70f * s, cx + 75f * s, h - 28f * s);
            smallTextPaint.Color = Color.LightGray;
            canvas.DrawText("MENÜ", cx, coopBtnRect.CenterY() + 7f * s, smallTextPaint);
        }

        private void DrawGarageScreen(Canvas canvas, float w, float h, float cx, float cy, float s)
        {
            textPaint.Color = Color.White;
            canvas.DrawText("GARAJ", cx, 65f * s, textPaint);

            prevMapRect.Set(cx - 125f * s, cy + 10f * s, cx - 8f * s, cy + 54f * s);
            nextMapRect.Set(cx + 8f * s, cy + 10f * s, cx + 125f * s, cy + 54f * s);
            bgPaint.Color = !engine.SelectedSportModel ? Color.Cyan : Color.Argb(200, 30, 40, 55);
            canvas.DrawRoundRect(prevMapRect, 20f * s, 20f * s, bgPaint);
            bgPaint.Color = engine.SelectedSportModel ? Color.Cyan : Color.Argb(200, 30, 40, 55);
            canvas.DrawRoundRect(nextMapRect, 20f * s, 20f * s, bgPaint);

            smallTextPaint.Color = !engine.SelectedSportModel ? Color.Black : Color.White;
            canvas.DrawText("STREET", prevMapRect.CenterX(), prevMapRect.CenterY() + 7f * s, smallTextPaint);
            smallTextPaint.Color = engine.SelectedSportModel ? Color.Black : Color.White;
            canvas.DrawText("SPORT", nextMapRect.CenterX(), nextMapRect.CenterY() + 7f * s, smallTextPaint);

            mainBtnRect.Set(cx - 95f * s, h - 130f * s, cx + 95f * s, h - 80f * s);
            accentPaint.Color = Color.Gold;
            canvas.DrawRoundRect(mainBtnRect, 24f * s, 24f * s, accentPaint);
            smallTextPaint.Color = Color.Black;
            canvas.DrawText("RENK DEĞİŞTİR", cx, mainBtnRect.CenterY() + 7f * s, smallTextPaint);

            coopBtnRect.Set(cx - 70f * s, h - 70f * s, cx + 70f * s, h - 28f * s);
            smallTextPaint.Color = Color.White;
            canvas.DrawText("GERİ", cx, coopBtnRect.CenterY() + 7f * s, smallTextPaint);
        }

        private void DrawSettingsScreen(Canvas canvas, float w, float h, float cx, float cy, float s)
        {
            textPaint.Color = Color.White;
            canvas.DrawText("AYARLAR", cx, 75f * s, textPaint);

            prevMapRect.Set(cx - 130f * s, 110f * s, cx + 130f * s, 162f * s);
            nextMapRect.Set(cx - 130f * s, 175f * s, cx + 130f * s, 227f * s);
            mainBtnRect.Set(cx - 130f * s, 240f * s, cx + 130f * s, 292f * s);

            bgPaint.Color = Color.Argb(220, 28, 38, 52);
            canvas.DrawRoundRect(prevMapRect, 20f * s, 20f * s, bgPaint);
            canvas.DrawRoundRect(nextMapRect, 20f * s, 20f * s, bgPaint);
            canvas.DrawRoundRect(mainBtnRect, 20f * s, 20f * s, bgPaint);

            smallTextPaint.Color = Color.Cyan;
            string sensName = engine.SensitivityLevel == 0 ? "DÜŞÜK" : (engine.SensitivityLevel == 2 ? "YÜKSEK" : "ORTA");
            canvas.DrawText($"HASSASİYET: {sensName}", cx, prevMapRect.CenterY() + 7f * s, smallTextPaint);
            canvas.DrawText($"GRAFİK: {(engine.HighQuality ? "YÜKSEK" : "PİL DOSTU")}", cx, nextMapRect.CenterY() + 7f * s, smallTextPaint);
            canvas.DrawText($"TİTREŞİM: {(engine.VibeEnabled ? "AÇIK" : "KAPALI")}", cx, mainBtnRect.CenterY() + 7f * s, smallTextPaint);

            coopBtnRect.Set(cx - 75f * s, h - 85f * s, cx + 75f * s, h - 35f * s);
            smallTextPaint.Color = Color.White;
            canvas.DrawText("GERİ", cx, coopBtnRect.CenterY() + 7f * s, smallTextPaint);
        }

        public override bool OnTouchEvent(MotionEvent e)
        {
            float x = e.GetX(), y = e.GetY();
            MotionEventActions act = e.ActionMasked;

            if (engine.State == GameState.PLAYING)
            {
                if (act == MotionEventActions.Up || act == MotionEventActions.Cancel)
                {
                    engine.TouchSteer = 0f;
                    return true;
                }

                if (act == MotionEventActions.Down)
                {
                    if (pauseBtnRect.Contains(x, y))
                    {
                        engine.TriggerHaptic(25);
                        engine.SetGameState(GameState.PAUSED);
                        return true;
                    }

                    float dx = x - Width * 0.5f, dy = y - Height * 0.5f;
                    if (turboBtnRect.Contains(x, y) || (dx * dx + dy * dy < 85f * 85f))
                    {
                        if (engine.PlayerCar != null && engine.PlayerCar.BoostReady)
                        {
                            engine.BoostRequest = true;
                            return true;
                        }
                    }
                }

                engine.TouchSteer = (x < Width * 0.5f) ? 0.05f : -0.05f;
                return true;
            }

            if (act != MotionEventActions.Up) return true;
            engine.TriggerHaptic(25);

            switch (engine.State)
            {
                case GameState.MENU:
                    if (mainBtnRect.Contains(x, y)) engine.StartRace(engine.SelectedMap);
                    else if (coopBtnRect.Contains(x, y)) engine.SetGameState(GameState.LOBBY);
                    else if (prevMapRect.Contains(x, y)) engine.SelectedMap = (engine.SelectedMap + 5) % 6;
                    else if (nextMapRect.Contains(x, y)) engine.SelectedMap = (engine.SelectedMap + 1) % 6;
                    else if (garageBtnRect.Contains(x, y)) engine.SetGameState(GameState.GARAGE);
                    else if (settingsBtnRect.Contains(x, y)) engine.SetGameState(GameState.SETTINGS);
                    break;

                case GameState.LOBBY:
                    if (prevMapRect.Contains(x, y)) engine.CoopManager.StartHostLobby();
                    else if (nextMapRect.Contains(x, y)) engine.CoopManager.StartAutoJoinScan();
                    else if (mainBtnRect.Contains(x, y)) engine.StartRace(engine.SelectedMap, broadcastToClients: true);
                    break;

                case GameState.PAUSED:
                    if (mainBtnRect.Contains(x, y)) engine.SetGameState(GameState.PLAYING);
                    else if (coopBtnRect.Contains(x, y)) engine.SetGameState(GameState.MENU);
                    break;

                case GameState.WIN:
                case GameState.GAME_OVER:
                    if (mainBtnRect.Contains(x, y)) engine.StartRace(engine.SelectedMap);
                    else if (coopBtnRect.Contains(x, y)) engine.SetGameState(GameState.MENU);
                    break;

                case GameState.GARAGE:
                    if (prevMapRect.Contains(x, y)) engine.SelectedSportModel = false;
                    else if (nextMapRect.Contains(x, y)) engine.SelectedSportModel = true;
                    else if (mainBtnRect.Contains(x, y)) engine.SelectedColorIndex = (engine.SelectedColorIndex + 1) % engine.CarPalette.Length;
                    else if (coopBtnRect.Contains(x, y)) engine.SetGameState(GameState.MENU);
                    break;

                case GameState.SETTINGS:
                    if (prevMapRect.Contains(x, y)) engine.SensitivityLevel = (engine.SensitivityLevel + 1) % 3;
                    else if (nextMapRect.Contains(x, y)) engine.HighQuality = !engine.HighQuality;
                    else if (mainBtnRect.Contains(x, y)) engine.VibeEnabled = !engine.VibeEnabled;
                    else if (coopBtnRect.Contains(x, y)) engine.SetGameState(GameState.MENU);
                    break;
            }

            Invalidate();
            return true;
        }
    }
}
