using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Net;
using System.Threading.Tasks;
using System.Windows.Forms;
using Rug.Osc;
using SharpDX.XInput;       // XInput 协议支持 (Xbox/PC模式)
using SharpDX.DirectInput;  // DirectInput 协议支持 (安卓/Switch/通用模式)

namespace VrcCameraDebugger
{
    // ==========================================
    // 1. 程序入口
    // ==========================================
    static class Program
    {
        [STAThread]
        static void Main()
        {
            Application.SetHighDpiMode(HighDpiMode.SystemAware);
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new DebugWindow());
        }
    }

    // ==========================================
    // 2. 自定义摇杆控件 (UI)
    // ==========================================
    public class JoystickControl : Control
    {
        public float ValueX { get; private set; } = 0f;
        public float ValueY { get; private set; } = 0f;
        public bool IsUserInteracting { get; private set; } = false;

        private Point centerPos;
        private Point knobPos;
        private int maxRadius = 0;

        public JoystickControl()
        {
            SetStyle(ControlStyles.SupportsTransparentBackColor, true);
            this.BackColor = Color.Transparent;
            this.Size = new Size(150, 150);
            this.DoubleBuffered = true;
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            centerPos = new Point(Width / 2, Height / 2);
            knobPos = centerPos;
            maxRadius = (Math.Min(Width, Height) / 2) - 20;
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            IsUserInteracting = true;
            UpdateKnob(e.Location);
            base.OnMouseDown(e);
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            if (IsUserInteracting) UpdateKnob(e.Location);
            base.OnMouseMove(e);
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            IsUserInteracting = false;
            knobPos = centerPos;
            ValueX = 0f;
            ValueY = 0f;
            this.Invalidate();
            base.OnMouseUp(e);
        }

        private void UpdateKnob(Point mouseLoc)
        {
            int dx = mouseLoc.X - centerPos.X;
            int dy = mouseLoc.Y - centerPos.Y;
            double distance = Math.Sqrt(dx * dx + dy * dy);

            if (distance > maxRadius)
            {
                double ratio = maxRadius / distance;
                dx = (int)(dx * ratio);
                dy = (int)(dy * ratio);
            }

            knobPos = new Point(centerPos.X + dx, centerPos.Y + dy);
            ValueX = dx / (float)maxRadius;
            ValueY = dy / (float)maxRadius;
            this.Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using (var brush = new SolidBrush(Color.FromArgb(50, 50, 50)))
                e.Graphics.FillEllipse(brush, centerPos.X - maxRadius, centerPos.Y - maxRadius, maxRadius * 2, maxRadius * 2);
            using (var pen = new Pen(Color.Gray, 2))
                e.Graphics.DrawEllipse(pen, centerPos.X - maxRadius, centerPos.Y - maxRadius, maxRadius * 2, maxRadius * 2);
            using (var brush = new SolidBrush(Color.IndianRed))
                e.Graphics.FillEllipse(brush, knobPos.X - 20, knobPos.Y - 20, 40, 40);
        }
    }

    // ==========================================
    // 3. 主窗口逻辑
    // ==========================================
    public class DebugWindow : Form
    {
        private OscSender? sender;
        private OscReceiver? receiver;
        private bool isListening = true;
        private System.Windows.Forms.Timer loopTimer;

        // --- XInput 手柄相关 ---
        private Controller xboxController = null!;
        private State previousXInputState;

        // --- DirectInput 手柄相关 ---
        private DirectInput directInput = null!;
        private Joystick dInputJoystick = null!;
        private JoystickState previousDInputState = null!;

        // --- 统一手柄状态 ---
        private bool isGamepadConnected = false;
        private string connectedProtocol = ""; // "XInput" 或 "DInput"
        private int gamepadCheckTimer = 0;
        private string lastGamepadUIStatus = ""; 

        // UI 基础控件
        private Label lblAddress, lblStatus, lblPosInfo, lblRotInfo, lblGamepadStatus;
        private TextBox txtSendAddress;
        private NumericUpDown numPosX, numPosY, numPosZ, numRotX, numRotY, numRotZ;
        private Button btnSend, btnReset;
        private JoystickControl joyMove, joyLook;
        private TrackBar trkSpeed;
        private Label lblSpeedValue;
        private CheckBox chkGimbalMode, chkLookAtMe, chkTopMost;
        private TextBox[] txtAuxAddrs;
        private CheckBox[] chkAuxEnables;
        private TrackBar? trkZoom, trkFocus, trkAperture, trkExposure, trkOpacity;
        private Label? lblZoomVal, lblFocusVal, lblApertureVal, lblExposureVal;
        private float[][] savedSlots = new float[3][]; 
        private Button[] btnSaves, btnLoads;

        public DebugWindow()
        {
            this.Text = "VRChat 无人机控制台 (支持 XInput & DInput 手柄)"; 
            this.Size = new Size(1450, 750); 
            this.StartPosition = FormStartPosition.CenterScreen;
            this.BackColor = Color.FromArgb(30, 30, 30);
            this.ForeColor = Color.White;
            this.DoubleBuffered = true;

            int margin = 20, col2 = 320, col3 = 600, col4 = 850, col5 = 1150; 

            // ================= 列 1：接收监视 与 重置 =================
            CreateLabel("--- [接收 RX] 端口 9001 ---", margin, 20, 14, Color.LightGreen);
            lblAddress = CreateLabel("等待数据...", margin, 50, 10, Color.Gray);
            CreateLabel("当前坐标回显:", margin, 80, 12, Color.Cyan);
            lblPosInfo = CreateLabel("X/Y/Z: 0.0 / 0.0 / 0.0", margin, 110, 12);
            lblRotInfo = CreateLabel("P/Y/R: 0.0 / 0.0 / 0.0", margin, 140, 12);

            btnReset = new Button() { Text = "重置原点 (Reset All)", Location = new Point(margin, 180), Size = new Size(180, 35), BackColor = Color.FromArgb(100, 40, 40), ForeColor = Color.White, FlatStyle = FlatStyle.Flat };
            btnReset.FlatAppearance.BorderSize = 0;
            btnReset.Click += (s, e) => ResetPositions();
            this.Controls.Add(btnReset);

            lblGamepadStatus = CreateLabel("🎮 智能设备: 扫描中...", margin, 670, 12, Color.Gray);

            // ================= 列 2：参数设置 =================
            CreateLabel("--- [参数设置] ---", col2, 20, 14, Color.LightSalmon);
            CreateLabel("目标地址:", col2, 50, 10);
            txtSendAddress = new TextBox() { Text = "/usercamera/Pose", Location = new Point(col2, 75), Size = new Size(200, 25) };
            this.Controls.Add(txtSendAddress);

            int inputY = 145, gapY = 40;
            CreateLabel("位置 (m):", col2, inputY - 25, 12, Color.Cyan);
            numPosX = CreateInput(col2 + 30, inputY, -10000, 10000);
            numPosY = CreateInput(col2 + 30, inputY + gapY, -10000, 10000);
            numPosZ = CreateInput(col2 + 30, inputY + gapY * 2, -10000, 10000);
            CreateLabel("X", col2 + 10, inputY + 5, 10); 
            CreateLabel("Y", col2 + 10, inputY + gapY + 5, 10); 
            CreateLabel("Z", col2 + 10, inputY + gapY * 2 + 5, 10);

            CreateLabel("旋转 (deg):", col2 + 140, inputY - 25, 12, Color.Orange);
            numRotX = CreateInput(col2 + 170, inputY, -360, 360);
            numRotY = CreateInput(col2 + 170, inputY + gapY, -360, 360);
            numRotZ = CreateInput(col2 + 170, inputY + gapY * 2, -360, 360);
            CreateLabel("P", col2 + 150, inputY + 5, 10); 
            CreateLabel("Y", col2 + 150, inputY + gapY + 5, 10); 
            CreateLabel("R", col2 + 150, inputY + gapY * 2 + 5, 10);

            btnSend = new Button() { Text = "直接设置当前位置", Location = new Point(col2, inputY + gapY * 3 + 10), Size = new Size(200, 40), BackColor = Color.FromArgb(60, 60, 60), ForeColor = Color.White };
            btnSend.Click += (s, e) => SendPoseData();
            this.Controls.Add(btnSend);
            lblStatus = CreateLabel("准备就绪", col2, inputY + gapY * 3 + 60, 10, Color.Gray);

            // ================= 列 3：摇杆控制 =================
            CreateLabel("--- [摇杆控制] ---", col3, 20, 14, Color.Yellow);
            CreateLabel("移动 (X / Z) [相对方向]", col3 + 35, 60, 10);
            joyMove = new JoystickControl() { Location = new Point(col3, 80) };
            this.Controls.Add(joyMove);

            chkGimbalMode = new CheckBox() { Text = "控制镜头俯仰 (Pitch)", Location = new Point(col3 + 35, 235), Size = new Size(200, 20), ForeColor = Color.LightSkyBlue };
            this.Controls.Add(chkGimbalMode);

            CreateLabel("升降/俯仰 & 旋转(Yaw)", col3 + 35, 260, 10);
            joyLook = new JoystickControl() { Location = new Point(col3, 280) };
            this.Controls.Add(joyLook);

            int sliderY = 450;
            CreateLabel("速度倍率:", col3, sliderY, 10, Color.LightSkyBlue);
            lblSpeedValue = CreateLabel("1.0x", col3 + 80, sliderY, 10, Color.LightSkyBlue);
            trkSpeed = new TrackBar() { Location = new Point(col3, sliderY + 25), Size = new Size(160, 45), Minimum = 1, Maximum = 50, Value = 10, TickFrequency = 5 };
            trkSpeed.Scroll += (s, e) => { lblSpeedValue.Text = $"{(trkSpeed.Value / 10.0f):F1}x"; };
            this.Controls.Add(trkSpeed);

            // ================= 列 4：模型参数同步 =================
            CreateLabel("--- [模型同步] ---", col4, 20, 14, Color.Violet);
            CreateLabel("同步相机数据给 Avatar", col4, 50, 10, Color.Gray);

            string[] labels = { "Pos X", "Pos Y", "Pos Z", "Rot P", "Rot Y", "Rot R" };
            string[] defaultParams = { "/avatar/parameters/DroneX", "/avatar/parameters/DroneY", "/avatar/parameters/DroneZ", "/avatar/parameters/DroneRotX", "/avatar/parameters/DroneRotY", "/avatar/parameters/DroneRotZ" };
            txtAuxAddrs = new TextBox[6]; chkAuxEnables = new CheckBox[6];
            for (int i = 0; i < 6; i++) {
                int currentY = 80 + (i * 50);
                CreateLabel(labels[i] + ":", col4, currentY + 4, 10, Color.Cyan);
                txtAuxAddrs[i] = new TextBox() { Text = defaultParams[i], Location = new Point(col4 + 50, currentY), Size = new Size(160, 23) };
                this.Controls.Add(txtAuxAddrs[i]);
                chkAuxEnables[i] = new CheckBox() { Text = "启用", Location = new Point(col4 + 220, currentY + 2), Size = new Size(60, 20), ForeColor = Color.White };
                this.Controls.Add(chkAuxEnables[i]);
            }

            // ================= 列 5：高级功能 =================
            CreateLabel("--- [高级功能] ---", col5, 20, 14, Color.Gold);
            int lensY = 50, lensGap = 85; 

            CreateLabel("变焦 (Zoom/FOV):", col5, lensY, 10, Color.White);
            lblZoomVal = CreateLabel("45", col5 + 180, lensY, 10, Color.LightGray);
            trkZoom = CreateSlider(col5, lensY + 25, 20, 150, 45, (s, e) => { SendOscFloat("/usercamera/Zoom", ((TrackBar)s!).Value); if(lblZoomVal != null) lblZoomVal.Text = ((TrackBar)s!).Value.ToString(); });

            CreateLabel("光圈 (Aperture):", col5, lensY + lensGap, 10, Color.White);
            lblApertureVal = CreateLabel("16.0", col5 + 180, lensY + lensGap, 10, Color.LightGray);
            trkAperture = CreateSlider(col5, lensY + lensGap + 25, 14, 320, 160, (s, e) => { float fStop = ((TrackBar)s!).Value / 10.0f; SendOscFloat("/usercamera/Aperture", fStop); if(lblApertureVal != null) lblApertureVal.Text = fStop.ToString("F1"); });

            CreateLabel("对焦距离 (Focus):", col5, lensY + lensGap * 2, 10, Color.White);
            lblFocusVal = CreateLabel("1.5m", col5 + 180, lensY + lensGap * 2, 10, Color.LightGray);
            trkFocus = CreateSlider(col5, lensY + lensGap * 2 + 25, 1, 100, 15, (s, e) => { float dist = ((TrackBar)s!).Value / 10.0f; SendOscFloat("/usercamera/FocalDistance", dist); if(lblFocusVal != null) lblFocusVal.Text = dist.ToString("F1") + "m"; });

            CreateLabel("曝光 (Exposure):", col5, lensY + lensGap * 3, 10, Color.White);
            lblExposureVal = CreateLabel("0", col5 + 180, lensY + lensGap * 3, 10, Color.LightGray);
            trkExposure = CreateSlider(col5, lensY + lensGap * 3 + 25, -100, 40, 0, (s, e) => { float ev = ((TrackBar)s!).Value / 10.0f; SendOscFloat("/usercamera/Exposure", ev); if(lblExposureVal != null) lblExposureVal.Text = ev.ToString("F1"); });

            int presetY = 380; CreateLabel("位置存档 (Presets):", col5, presetY, 12, Color.Gold);
            btnSaves = new Button[3]; btnLoads = new Button[3];
            for(int i=0; i<3; i++) {
                int py = presetY + 30 + (i * 40); int slotIndex = i; 
                btnSaves[i] = new Button() { Text = $"存 ({i+1})", Location = new Point(col5, py), Size = new Size(90, 30), BackColor = Color.DimGray, ForeColor = Color.White };
                btnSaves[i].Click += (s, e) => SavePreset(slotIndex); this.Controls.Add(btnSaves[i]);
                btnLoads[i] = new Button() { Text = $"读 ({i+1})", Location = new Point(col5 + 100, py), Size = new Size(90, 30), BackColor = Color.DimGray, ForeColor = Color.White };
                btnLoads[i].Click += (s, e) => LoadPreset(slotIndex); this.Controls.Add(btnLoads[i]);
            }

            int miscY = 530; CreateLabel("智能追踪:", col5, miscY, 12, Color.Gold);
            chkLookAtMe = new CheckBox() { Text = "Look At Me (自动对脸)", Location = new Point(col5, miscY + 30), Size = new Size(200, 25), ForeColor = Color.White };
            chkLookAtMe.CheckedChanged += (s, e) => SendOscBool("/usercamera/LookAtMe", chkLookAtMe.Checked);
            this.Controls.Add(chkLookAtMe);

            int winY = 600; CreateLabel("窗口设置:", col5, winY, 12, Color.Gold);
            chkTopMost = new CheckBox() { Text = "窗口置顶 (Always Top)", Location = new Point(col5, winY + 30), Size = new Size(200, 25), ForeColor = Color.White };
            chkTopMost.CheckedChanged += (s, e) => { this.TopMost = chkTopMost.Checked; };
            this.Controls.Add(chkTopMost);
            CreateLabel("透明度:", col5, winY + 60, 10);
            trkOpacity = CreateSlider(col5, winY + 85, 20, 100, 100, (s, e) => { this.Opacity = ((TrackBar)s!).Value / 100.0; }); trkOpacity.Width = 200;

            loopTimer = new System.Windows.Forms.Timer() { Interval = 15 };
            loopTimer.Tick += LoopTimer_Tick;
        }

        protected override void OnLoad(EventArgs e)
        {
            base.OnLoad(e);
            directInput = new DirectInput(); // 初始化 D-Input 引擎
            InitializeNetwork();
            EnableVRChatSmoothing();
            loopTimer.Start();
        }

        // --- 核心更新循环 ---
        private void LoopTimer_Tick(object? sender, EventArgs e)
        {
            float inputX = 0f, inputY = 0f, lookX = 0f, lookY = 0f;   
            bool hasAnyInput = false;

            // A. 读取 UI 摇杆
            if (joyMove.IsUserInteracting) { inputX = joyMove.ValueX; inputY = -joyMove.ValueY; hasAnyInput = true; }
            if (joyLook.IsUserInteracting) { lookX = joyLook.ValueX; lookY = joyLook.ValueY; hasAnyInput = true; }

            // B. 尝试寻找/读取手柄
            if (!isGamepadConnected)
            {
                gamepadCheckTimer++;
                if (gamepadCheckTimer > 60) // 大约每秒扫描一次硬件
                {
                    TryConnectGamepad();
                    gamepadCheckTimer = 0;
                }
            }
            else
            {
                if (connectedProtocol == "XInput")
                {
                    if (!ReadXInput(ref inputX, ref inputY, ref lookX, ref lookY, ref hasAnyInput))
                        DisconnectGamepad();
                }
                else if (connectedProtocol == "DInput")
                {
                    if (!ReadDInput(ref inputX, ref inputY, ref lookX, ref lookY, ref hasAnyInput))
                        DisconnectGamepad();
                }
            }

            // C. 更新 UI 连接状态
            string currentStatusText = isGamepadConnected ? $"🎮 手柄: 已接入 ({connectedProtocol})" : "🎮 手柄: 未接入";
            if (currentStatusText != lastGamepadUIStatus)
            {
                lblGamepadStatus.Text = currentStatusText;
                lblGamepadStatus.ForeColor = isGamepadConnected ? Color.LimeGreen : Color.Gray;
                lastGamepadUIStatus = currentStatusText;
            }

            if (!hasAnyInput) return;

            // --- 运动解算 ---
            float speedMultiplier = trkSpeed.Value / 10.0f;
            float moveSpeed = 0.15f * speedMultiplier; 
            float rotSpeed = 1.5f * speedMultiplier;

            float inputStrafe = inputX * moveSpeed; 
            float inputForward = inputY * moveSpeed; 
            double angleRad = (float)numRotY.Value * Math.PI / 180.0;

            float globalDeltaX = (float)(inputStrafe * Math.Cos(angleRad) + inputForward * Math.Sin(angleRad));
            float globalDeltaZ = (float)(inputForward * Math.Cos(angleRad) - inputStrafe * Math.Sin(angleRad));

            float newY = (float)numPosY.Value;
            float newPitch = (float)numRotX.Value;

            if (chkGimbalMode.Checked)
                newPitch = (float)numRotX.Value + (lookY * rotSpeed); 
            else
                newY = (float)numPosY.Value - (lookY * moveSpeed);

            float newYaw = (float)numRotY.Value + (lookX * rotSpeed);
            float newX = (float)numPosX.Value + globalDeltaX;
            float newZ = (float)numPosZ.Value + globalDeltaZ;

            numPosX.Value = Clamp(newX, (float)numPosX.Minimum, (float)numPosX.Maximum);
            numPosZ.Value = Clamp(newZ, (float)numPosZ.Minimum, (float)numPosZ.Maximum);
            numPosY.Value = Clamp(newY, (float)numPosY.Minimum, (float)numPosY.Maximum);
            numRotX.Value = Clamp(newPitch, -90, 90);
            numRotY.Value = Clamp(newYaw, -360, 360);

            SendPoseData();
        }

        // =====================================
        // 双协议智能热插拔检测逻辑
        // =====================================
        private void TryConnectGamepad()
        {
            // 1. 优先尝试 XInput (Xbox标准协议)
            Controller[] xControllers = { new Controller(UserIndex.One), new Controller(UserIndex.Two), new Controller(UserIndex.Three), new Controller(UserIndex.Four) };
            foreach (var c in xControllers)
            {
                if (c.IsConnected)
                {
                    xboxController = c;
                    connectedProtocol = "XInput";
                    isGamepadConnected = true;
                    previousXInputState = xboxController.GetState();
                    return;
                }
            }

            // 2. 如果没有XInput，尝试 DirectInput (通用/安卓/Switch协议)
            var dInputDevices = directInput.GetDevices(SharpDX.DirectInput.DeviceType.Gamepad, DeviceEnumerationFlags.AllDevices);
            if (dInputDevices.Count == 0) dInputDevices = directInput.GetDevices(SharpDX.DirectInput.DeviceType.Joystick, DeviceEnumerationFlags.AllDevices);

            if (dInputDevices.Count > 0)
            {
                try {
                    dInputJoystick = new Joystick(directInput, dInputDevices[0].InstanceGuid);
                    dInputJoystick.Properties.BufferSize = 128;
                    dInputJoystick.Acquire();
                    dInputJoystick.Poll();
                    previousDInputState = dInputJoystick.GetCurrentState();
                    connectedProtocol = "DInput";
                    isGamepadConnected = true;
                } 
                catch { DisconnectGamepad(); }
            }
        }

        private void DisconnectGamepad()
        {
            isGamepadConnected = false;
            connectedProtocol = "";
            try { dInputJoystick?.Unacquire(); dInputJoystick?.Dispose(); } catch { }
        }

        // =====================================
        // XInput 读取逻辑 (原生 Xbox 模式)
        // =====================================
        private bool ReadXInput(ref float inputX, ref float inputY, ref float lookX, ref float lookY, ref bool hasAnyInput)
        {
            if (!xboxController.IsConnected) return false;
            
            State state;
            if (!xboxController.GetState(out state)) return false;

            Gamepad gp = state.Gamepad;
            float gpMoveX = ApplyDeadzone(gp.LeftThumbX, Gamepad.LeftThumbDeadZone);
            float gpMoveY = ApplyDeadzone(gp.LeftThumbY, Gamepad.LeftThumbDeadZone); 
            float gpLookX = ApplyDeadzone(gp.RightThumbX, Gamepad.RightThumbDeadZone);
            float gpLookY = ApplyDeadzone(gp.RightThumbY, Gamepad.RightThumbDeadZone); 

            if (Math.Abs(gpMoveX) > 0 || Math.Abs(gpMoveY) > 0) { inputX = gpMoveX; inputY = gpMoveY; hasAnyInput = true; }
            if (Math.Abs(gpLookX) > 0 || Math.Abs(gpLookY) > 0) { lookX = gpLookX; lookY = -gpLookY; hasAnyInput = true; }

            // 变焦 (Zoom)
            if (trkZoom != null) {
                if (gp.Buttons.HasFlag(GamepadButtonFlags.RightShoulder) && trkZoom.Value > trkZoom.Minimum) { trkZoom.Value -= 1; hasAnyInput = true; }
                else if (gp.Buttons.HasFlag(GamepadButtonFlags.LeftShoulder) && trkZoom.Value < trkZoom.Maximum) { trkZoom.Value += 1; hasAnyInput = true; }
                SendOscFloat("/usercamera/Zoom", trkZoom.Value);
            }

            // 单次按键
            if (state.PacketNumber != previousXInputState.PacketNumber) {
                if (gp.Buttons.HasFlag(GamepadButtonFlags.A) && !previousXInputState.Gamepad.Buttons.HasFlag(GamepadButtonFlags.A)) chkGimbalMode.Checked = !chkGimbalMode.Checked;
                if (gp.Buttons.HasFlag(GamepadButtonFlags.Start) && !previousXInputState.Gamepad.Buttons.HasFlag(GamepadButtonFlags.Start)) ResetPositions();
                if (gp.Buttons.HasFlag(GamepadButtonFlags.DPadUp) && !previousXInputState.Gamepad.Buttons.HasFlag(GamepadButtonFlags.DPadUp)) if (trkSpeed.Value < trkSpeed.Maximum) trkSpeed.Value += 1;
                if (gp.Buttons.HasFlag(GamepadButtonFlags.DPadDown) && !previousXInputState.Gamepad.Buttons.HasFlag(GamepadButtonFlags.DPadDown)) if (trkSpeed.Value > trkSpeed.Minimum) trkSpeed.Value -= 1;
            }
            previousXInputState = state;
            return true;
        }

        // =====================================
        // D-Input 读取逻辑 (安卓/Switch模式)
        // =====================================
        private bool ReadDInput(ref float inputX, ref float inputY, ref float lookX, ref float lookY, ref bool hasAnyInput)
        {
            try {
                dInputJoystick.Poll();
                var state = dInputJoystick.GetCurrentState();

                // D-Input 的摇杆值通常是 0 到 65535。中心值是 32767。
                float NormalizeAxis(int value) 
                {
                    float normalized = (value - 32767) / 32767.0f;
                    if (Math.Abs(normalized) < 0.15f) return 0f;
                    return normalized;
                }

                float gpMoveX = NormalizeAxis(state.X);
                float gpMoveY = -NormalizeAxis(state.Y); 
                float gpLookX = NormalizeAxis(state.Z);
                float gpLookY = NormalizeAxis(state.RotationZ); 

                if (Math.Abs(gpMoveX) > 0 || Math.Abs(gpMoveY) > 0) { inputX = gpMoveX; inputY = gpMoveY; hasAnyInput = true; }
                if (Math.Abs(gpLookX) > 0 || Math.Abs(gpLookY) > 0) { lookX = gpLookX; lookY = gpLookY; hasAnyInput = true; }

                if (trkZoom != null) {
                    if (state.Buttons[5] && trkZoom.Value > trkZoom.Minimum) { trkZoom.Value -= 1; hasAnyInput = true; }
                    else if (state.Buttons[4] && trkZoom.Value < trkZoom.Maximum) { trkZoom.Value += 1; hasAnyInput = true; }
                    SendOscFloat("/usercamera/Zoom", trkZoom.Value);
                }

                if (state.Buttons[0] && !previousDInputState.Buttons[0]) chkGimbalMode.Checked = !chkGimbalMode.Checked;
                if (state.Buttons[7] && !previousDInputState.Buttons[7]) ResetPositions();
                
                int pov = state.PointOfViewControllers[0];
                int prevPov = previousDInputState.PointOfViewControllers[0];
                if (pov == 0 && prevPov != 0) if (trkSpeed.Value < trkSpeed.Maximum) trkSpeed.Value += 1; 
                if (pov == 18000 && prevPov != 18000) if (trkSpeed.Value > trkSpeed.Minimum) trkSpeed.Value -= 1; 

                previousDInputState = state;
                return true;
            }
            catch { return false; }
        }

        // =====================================
        // 核心修复点：将 short 强转为 int，防止 OverflowException
        // =====================================
        private float ApplyDeadzone(short value, int deadzone) 
        { 
            if (Math.Abs((int)value) < deadzone) return 0f; 
            return (float)value / 32768f; 
        }

        private void ResetPositions() {
            if (MessageBox.Show("确定要重置吗？", "确认重置", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) == DialogResult.Yes) {
                numPosX.Value = 0; numPosY.Value = 0; numPosZ.Value = 0;
                numRotX.Value = 0; numRotY.Value = 0; numRotZ.Value = 0;
                SendPoseData(); 
                if (trkZoom != null) { trkZoom.Value = 45; SendOscFloat("/usercamera/Zoom", 45); }
            }
        }

        private void SavePreset(int slot) { savedSlots[slot] = new float[] { (float)numPosX.Value, (float)numPosY.Value, (float)numPosZ.Value, (float)numRotX.Value, (float)numRotY.Value, (float)numRotZ.Value }; }
        private void LoadPreset(int slot) { if (savedSlots[slot] == null) return; var data = savedSlots[slot]; numPosX.Value = (decimal)data[0]; numPosY.Value = (decimal)data[1]; numPosZ.Value = (decimal)data[2]; numRotX.Value = (decimal)data[3]; numRotY.Value = (decimal)data[4]; numRotZ.Value = (decimal)data[5]; SendPoseData(); }
        private decimal Clamp(float val, float min, float max) => (decimal)(val < min ? min : (val > max ? max : val));
        private void SendOscFloat(string addr, float val) { if (sender != null) try { sender.Send(new OscMessage(addr, val)); } catch {} }
        private void SendOscBool(string addr, bool val) { if (sender != null) try { sender.Send(new OscMessage(addr, val)); } catch {} }

        private void SendPoseData() {
            if (this.sender == null) return;
            try {
                this.sender.Send(new OscMessage(txtSendAddress.Text, (float)numPosX.Value, (float)numPosY.Value, (float)numPosZ.Value, (float)numRotX.Value, (float)numRotY.Value, (float)numRotZ.Value));
                float[] cv = { (float)numPosX.Value, (float)numPosY.Value, (float)numPosZ.Value, (float)numRotX.Value, (float)numRotY.Value, (float)numRotZ.Value };
                for (int i = 0; i < 6; i++) if (chkAuxEnables[i].Checked) this.sender.Send(new OscMessage(txtAuxAddrs[i].Text.Trim(), cv[i]));
                lblStatus.Text = "正在发送..."; lblStatus.ForeColor = Color.Lime;
            } catch { lblStatus.Text = "发送失败"; lblStatus.ForeColor = Color.Red; }
        }

        private void EnableVRChatSmoothing() { SendOscBool("/usercamera/SmoothMovement", true); SendOscFloat("/usercamera/SmoothingStrength", 5.0f); }

        private void InitializeNetwork() {
            Task.Run(() => {
                receiver = new OscReceiver(9001);
                try {
                    receiver.Connect();
                    while (isListening) {
                        var packet = receiver.Receive();
                        if (packet is OscMessage msg && this.IsHandleCreated && !this.Disposing) this.Invoke(new Action(() => UpdateRxUI(msg)));
                    }
                } catch { }
            });
            try { sender = new OscSender(IPAddress.Loopback, 0, 9000); sender.Connect(); } catch {}
        }

        private void UpdateRxUI(OscMessage msg) {
            if (msg.Address.Contains("Pose") && msg.Count >= 6) {
                if (msg[0] is float x && msg[1] is float y && msg[2] is float z && msg[3] is float pitch && msg[4] is float yaw && msg[5] is float roll) {
                    lblPosInfo.Text = $"X: {x:F2}  Y: {y:F2}  Z: {z:F2}"; lblRotInfo.Text = $"P: {pitch:F1}  Y: {yaw:F1}  R: {roll:F1}";
                    if (!joyMove.IsUserInteracting && !joyLook.IsUserInteracting && !isGamepadConnected) {
                        if (Math.Abs((float)numPosX.Value - x) > 0.05f) numPosX.Value = Clamp(x, -10000, 10000);
                        if (Math.Abs((float)numPosY.Value - y) > 0.05f) numPosY.Value = Clamp(y, -10000, 10000);
                        if (Math.Abs((float)numPosZ.Value - z) > 0.05f) numPosZ.Value = Clamp(z, -10000, 10000);
                    }
                }
            }
        }

        private Label CreateLabel(string text, int x, int y, int fontSize, Color? c = null) { Label l = new Label() { Text = text, Location = new Point(x, y), AutoSize = true, Font = new Font("Microsoft YaHei UI", fontSize, FontStyle.Regular), ForeColor = c ?? Color.White }; this.Controls.Add(l); return l; }
        private NumericUpDown CreateInput(int x, int y, int min, int max) { NumericUpDown num = new NumericUpDown() { Location = new Point(x, y), Size = new Size(80, 25), DecimalPlaces = 2, Minimum = min, Maximum = max, Increment = 0.5M }; this.Controls.Add(num); return num; }
        private TrackBar CreateSlider(int x, int y, int min, int max, int val, EventHandler scrollAction) { TrackBar tb = new TrackBar() { Location = new Point(x, y), Size = new Size(180, 45), Minimum = min, Maximum = max, Value = val, TickFrequency = (max-min)/10 }; tb.Scroll += scrollAction; this.Controls.Add(tb); return tb; }

        protected override void OnFormClosing(FormClosingEventArgs e) {
            isListening = false; loopTimer?.Stop(); loopTimer?.Dispose(); DisconnectGamepad(); try { receiver?.Close(); } catch { } try { sender?.Close(); } catch { } base.OnFormClosing(e);
        }
    }
}